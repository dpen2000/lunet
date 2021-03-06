﻿// Copyright (c) Alexandre Mutel. All rights reserved.
// This file is licensed under the BSD-Clause 2 license. 
// See the license.txt file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lunet.Core;
using Lunet.Helpers;
using Scriban;
using Scriban.Syntax;
using Scriban.Parsing;
using Scriban.Runtime;
using Zio;

namespace Lunet.Scripts
{
    public class ScriptingPlugin : SitePlugin
    {
        private readonly ITemplateLoader unauthorizedTemplateLoader;

        private const string IncludesDirectoryName = "includes";

        internal ScriptingPlugin(SiteObject site) : base(site)
        {
            unauthorizedTemplateLoader = new TemplateLoaderUnauthorized(Site);
            Builtins = TemplateContext.GetDefaultBuiltinObject();
            SiteFunctions = new ScriptGlobalFunctions(this);
            // Add default scriban frontmatter parser
            FrontMatterParsers = new OrderedList<IFrontMatterParser> {new ScribanFrontMatterParser(this)};
        }

        public ScriptObject Builtins { get; }
        
        public OrderedList<IFrontMatterParser> FrontMatterParsers { get; }

        /// <summary>
        /// Gets the functions that are only accessible from a sban/script file (and not from a page)
        /// </summary>
        public ScriptGlobalFunctions SiteFunctions { get; }

        /// <summary>
        /// Parses a script with the specified content and path.
        /// </summary>
        /// <param name="scriptContent">Content of the script.</param>
        /// <param name="scriptPath">The script path.</param>
        /// <param name="parsingMode">The parsing mode.</param>
        /// <returns>
        /// The parsed script or null of an error occured
        /// </returns>
        /// <exception cref="System.ArgumentNullException">if <paramref name="scriptContent" /> or <paramref name="scriptPath" /> is null</exception>
        /// <remarks>
        /// If there are any parsing errors, the errors will be logged to the <see cref="Log" /> and <see cref="HasErrors" /> will be set to <c>true</c>.
        /// </remarks>
        public ScriptInstance ParseScript(string scriptContent, UPath scriptPath, ScriptMode parsingMode)
        {
            if (scriptContent == null) throw new ArgumentNullException(nameof(scriptContent));
            if (scriptPath == null) throw new ArgumentNullException(nameof(scriptPath));

            IFrontMatter frontmatter = null;
            TextPosition startPosition = default;
            if (parsingMode == ScriptMode.FrontMatterAndContent && scriptContent.Length > 3)
            {
                var span = scriptContent.AsSpan();
                foreach (var parser in FrontMatterParsers)
                {
                    if (parser.CanHandle(span))
                    {
                        frontmatter = parser.TryParse(scriptContent, (string) scriptPath, out startPosition);
                        break;
                    }
                }
                parsingMode = ScriptMode.Default;
            }

            // Load parse the template
            var template = Template.Parse(scriptContent, scriptPath.FullName, null, new LexerOptions() { StartPosition = startPosition, Mode = parsingMode });

            // If we have any errors, log them and set the errors flag on this instance
            if (template.HasErrors)
            {
                LogScriptMessages(template.Messages);
            }

            return new ScriptInstance(template.HasErrors, (string)scriptPath, frontmatter, template.Page);
        }

        public bool TryImportScript(string scriptText, UPath scriptPath, IDynamicObject scriptObject, ScriptFlags flags, out object result, TemplateContext context = null)
        {
            if (scriptText == null) throw new ArgumentNullException(nameof(scriptText));
            if (scriptPath == null) throw new ArgumentNullException(nameof(scriptPath));
            if (scriptObject == null) throw new ArgumentNullException(nameof(scriptObject));

            result = null;
            context = context ?? new TemplateContext(Builtins);

            var scriptResult = ParseScript(scriptText, scriptPath.FullName, ScriptMode.ScriptOnly);
            if (!scriptResult.HasErrors)
            {
                if ((flags & ScriptFlags.AllowSiteFunctions) != 0)
                {
                    context.PushGlobal(SiteFunctions);
                }

                context.PushGlobal((ScriptObject)scriptObject);
                context.PushSourceFile(scriptPath.FullName);
                context.EnableOutput = false;
                context.TemplateLoader =  (flags & ScriptFlags.AllowSiteFunctions) != 0 ? unauthorizedTemplateLoader : new TemplateLoaderFromIncludes(Site);

                try
                {
                    result = scriptResult.Template.Evaluate(context);
                }
                catch (ScriptRuntimeException exception)
                {
                    LogException(exception);
                    return false;
                }
                finally
                {
                    context.PopSourceFile();
                    context.PopGlobal();
                    if ((flags & ScriptFlags.AllowSiteFunctions) != 0)
                    {
                        context.PopGlobal();
                    }
                }
                return true;
            }
            return false;
        }

        public bool TryImportScriptStatement(string scriptStatement, IDynamicObject scriptObject, ScriptFlags flags, out object result, TemplateContext context = null)
        {
            if (scriptStatement == null) throw new ArgumentNullException(nameof(scriptStatement));
            if (scriptObject == null) throw new ArgumentNullException(nameof(scriptObject));
            return TryImportScript(scriptStatement, "__script__", scriptObject, flags, out result, context);
        }

        public bool TryImportScriptFromFile(FileEntry scriptPath, IDynamicObject scriptObject, ScriptFlags flags, out object result, TemplateContext context = null)
        {
            if (scriptPath == null) throw new ArgumentNullException(nameof(scriptPath));
            if (scriptObject == null) throw new ArgumentNullException(nameof(scriptObject));
            result = null;

            var scriptExist = scriptPath.Exists;
            if ((flags & ScriptFlags.Expect) != 0)
            {
                if (!scriptExist)
                {
                    Site.Error($"Config file [{scriptPath}] does not exist");
                    return false;
                }
            }

            if (scriptExist)
            {
                var configAsText = scriptPath.ReadAllText();
                return TryImportScript(configAsText, scriptPath.Path, scriptObject, flags, out result, context);
            }
            return true;
        }

        /// <summary>
        /// Initializes this instance.
        /// </summary>
        public bool TryRunFrontMatter(IFrontMatter frontMatter, ContentObject obj)
        {
            if (frontMatter == null) throw new ArgumentNullException(nameof(frontMatter));
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            
            var context = new TemplateContext(Builtins);
            context.PushGlobal(obj);
            try
            {
                context.EnableOutput = false;
                context.TemplateLoader = new TemplateLoaderFromIncludes(Site);

                Site.SetValue(PageVariables.Site, this, true);
                frontMatter.Evaluate(context);
            }
            catch (ScriptRuntimeException exception)
            {
                LogException(exception);
                return false;
            }
            finally
            {
                context.PopGlobal();

                // We don't keep the site variable after this initialization
                Site.Remove(PageVariables.Site);
            }
            return true;
        }


        /// <summary>
        /// Initializes this instance.
        /// </summary>
        public bool TryEvaluate(ContentObject page, ScriptPage script, UPath scriptPath, ScriptObject obj = null, TemplateContext context = null)
        {
            if (page == null) throw new ArgumentNullException(nameof(page));
            if (script == null) throw new ArgumentNullException(nameof(script));
            if (scriptPath == null) throw new ArgumentNullException(nameof(scriptPath));

            context = context ?? new TemplateContext(Builtins);
            if (obj != null)
            {
                context.PushGlobal(obj);
            }
            context.PushSourceFile((string)scriptPath);

            var currentScriptObject = (ScriptObject)context.CurrentGlobal;

            try
            {
                context.EnableOutput = true;
                var includeLoader = new TemplateLoaderFromIncludes(Site);
                context.TemplateLoader = includeLoader;

                currentScriptObject.SetValue(PageVariables.Site, Site, true);
                currentScriptObject.SetValue(PageVariables.Page, page, true);

                // TODO: setup include paths for script
                script.Evaluate(context);

                foreach (var includeFile in includeLoader.IncludeFiles)
                {
                    page.Dependencies.Add(new FileContentDependency(includeFile));
                }

            }
            catch (ScriptRuntimeException exception)
            {
                LogException(exception);
                return false;
            }
            finally
            {
                if (obj != null)
                {
                    context.PopGlobal();
                }
                context.PopSourceFile();
                page.Content = context.Output.ToString();

                // We don't keep the site variable after this initialization
                currentScriptObject.Remove(PageVariables.Site);
                currentScriptObject.Remove(PageVariables.Page);
            }
            return true;
        }

        private void LogException(ScriptRuntimeException exception)
        {
            Site.Error(exception.Span, exception.GetReason());

            var parserException = exception as ScriptParserRuntimeException;
            if (parserException != null)
            {
                LogScriptMessages(parserException.ParserMessages);
            }
        }

        private void LogScriptMessages(LogMessageBag logMessages)
        {
            foreach (var logMessage in logMessages)
            {
                switch (logMessage.Type)
                {
                    case ParserMessageType.Error:
                        Site.Error(logMessage.Span, logMessage.Message);
                        break;

                    case ParserMessageType.Warning:
                        Site.Warning(logMessage.Span, logMessage.Message);
                        break;
                    default:
                        Site.Info(logMessage.Span, logMessage.Message);
                        break;
                }
            }
        }

        private class TemplateLoaderUnauthorized : ITemplateLoader
        {
            private readonly SiteObject site;

            public TemplateLoaderUnauthorized(SiteObject site)
            {
                this.site = site;
            }

            public string GetPath(TemplateContext context, SourceSpan callerSpan, string templateName)
            {
                site.Error(callerSpan, $"The include statement is not allowed from this context. The include [{templateName}] cannot be loaded");
                return null;
            }

            public string Load(TemplateContext context, SourceSpan callerSpan, string templatePath)
            {
                throw new NotImplementedException();
            }

            public ValueTask<string> LoadAsync(TemplateContext context, SourceSpan callerSpan, string templatePath)
            {
                return new ValueTask<string>(Load(context, callerSpan, templatePath));
            }
        }

        private class TemplateLoaderFromIncludes : ITemplateLoader
        {
            private readonly SiteObject site;

            public TemplateLoaderFromIncludes(SiteObject site)
            {
                this.site = site;
                IncludeFiles = new HashSet<FileEntry>();
            }

            public HashSet<FileEntry> IncludeFiles { get; }

            public string GetPath(TemplateContext context, SourceSpan callerSpan, string templateName)
            {
                templateName = templateName.Trim();
                if (templateName.Contains("..") || templateName.StartsWith("/") || templateName.StartsWith("\\"))
                {
                    site.Error(callerSpan, $"The include [{templateName}] cannot contain '..' or start with '/' or '\\'");
                    return null;
                }
                var templatePath = UPath.Root / IncludesDirectoryName / templateName;
                return (string) templatePath;
            }

            public string Load(TemplateContext context, SourceSpan callerSpan, string templatePath)
            {
                var templateFile = new FileEntry(site.MetaFileSystem, templatePath);
                IncludeFiles.Add(templateFile);
                return templateFile.ReadAllText();
            }

            public ValueTask<string> LoadAsync(TemplateContext context, SourceSpan callerSpan, string templatePath)
            {
                return new ValueTask<string>(Load(context, callerSpan, templatePath));
            }
        }
        
        private class ScribanFrontMatter : IFrontMatter
        {
            public ScribanFrontMatter(ScriptFrontMatter frontmatter)
            {
                Value = frontmatter;
            }

            public ScriptFrontMatter Value { get; }

            public void Evaluate(TemplateContext context)
            {
                context.Evaluate(Value);
            }
        }
        
        private class ScribanFrontMatterParser : IFrontMatterParser
        {
            private readonly ScriptingPlugin _scripting;

            public ScribanFrontMatterParser(ScriptingPlugin scripting)
            {
                _scripting = scripting;
            }
            
            public bool CanHandle(ReadOnlySpan<char> header)
            {
                return header[0] == '+' && header[1] == '+' && header[2] == '+';
            }

            public IFrontMatter TryParse(string text, string sourceFilePath, out TextPosition position)
            {
                position = default;
                var frontMatter = Template.Parse(text, sourceFilePath, lexerOptions: new LexerOptions() {FrontMatterMarker = "+++", Mode = ScriptMode.FrontMatterOnly});
                if (frontMatter.HasErrors || frontMatter.Page.FrontMatter == null)
                {
                    _scripting.LogScriptMessages(frontMatter.Messages);
                    return null;
                }
                position = frontMatter.Page.FrontMatter.TextPositionAfterEndMarker;
                return new ScribanFrontMatter(frontMatter.Page.FrontMatter);
            }
        }
    }
}