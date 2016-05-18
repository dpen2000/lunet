﻿// Copyright (c) Alexandre Mutel. All rights reserved.
// This file is licensed under the BSD-Clause 2 license. 
// See the license.txt file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Lunet.Bundles;
using Lunet.Extends;
using Lunet.Helpers;
using Lunet.Plugins;
using Lunet.Resources;
using Lunet.Scripts;
using Lunet.Statistics;
using Microsoft.Extensions.Logging;
using Scriban.Parsing;

namespace Lunet.Core
{
    public class SiteObject : LunetObject
    {
        private const string SiteDirectoryName = "_site";
        private const string PrivateDirectoryName = ".lunet";
        private const string DefaultPageExtensionValue = ".html";
        private readonly Stopwatch clock;

        public SiteObject(string configFilePathArg, ILoggerFactory loggerFactory = null)
        {
            if (string.IsNullOrEmpty(configFilePathArg)) throw new ArgumentNullException(nameof(configFilePathArg));

            // Make sure to get a proper config file path
            this.ConfigFile = new FileInfo(configFilePathArg).FullName;
            var baseDirectoryFullpath = Path.GetDirectoryName(ConfigFile);
            if (baseDirectoryFullpath == null)
            {
                throw new ArgumentException($"Cannot find parent directory of config file [{ConfigFile}]");
            }
            BaseDirectory = baseDirectoryFullpath;
            PrivateBaseDirectory = Path.Combine(baseDirectoryFullpath, PrivateDirectoryName);

            clock = new Stopwatch();

            StaticFiles = new PageCollection();
            Pages = new PageCollection();
            DynamicPages = new PageCollection();

            // Plugins

            // Create the logger
            LoggerFactory = loggerFactory ?? new LoggerFactory();
            LoggerFactory.AddProvider(new LoggerProviderIntercept(this));
            Log = LoggerFactory.CreateLogger("lunet");
            ContentTypes = new ContentTypeManager();

            OutputDirectory = PrivateBaseDirectory.GetSubFolder(SiteDirectoryName);

            DefaultPageExtension = DefaultPageExtensionValue;

            Statistics = new SiteStatistics();

            Scripts = new ScriptManager(this);
            Generator = new SiteGenerator(this);

            // LoadConfig the plugins after they have been loaded/modified from the config
            Managers = new OrderedList<ManagerBase>()
            {
                (Meta = new MetaManager(this)),
                Scripts,
                (Extends = new ExtendManager(this)),
                (Plugins = new PluginManager(this)),
                (Resources = new ResourceManager(this)),
                (Bundles = new BundleManager(this))
            };
        }

        public string ConfigFile { get; }

        public FolderInfo BaseDirectory { get; }

        public FolderInfo PrivateBaseDirectory { get; }

        public FolderInfo OutputDirectory { get; set; }

        /// <summary>
        /// Gets the logger factory that was used to create the site logger <see cref="Log"/>.
        /// </summary>
        public ILoggerFactory LoggerFactory { get; }

        /// <summary>
        /// Gets the site logger.
        /// </summary>
        public ILogger Log { get; }

        public PageCollection StaticFiles { get; }

        public PageCollection DynamicPages { get; }

        public PageCollection Pages { get; }

        public bool HasErrors { get; set; }

        internal List<ManagerBase> Managers { get; }

        public MetaManager Meta { get; }

        public PluginManager Plugins { get; }

        public ExtendManager Extends { get; }

        public SiteGenerator Generator { get; }

        public ScriptManager Scripts { get; }

        public ResourceManager Resources { get; }

        public BundleManager Bundles { get; }

        public SiteStatistics Statistics { get; }

        public ContentTypeManager ContentTypes { get; }


        public bool UrlAsFile
        {
            get { return GetSafeValue<bool>(SiteVariables.UrlAsFile); }
            set { this[SiteVariables.UrlAsFile] = value; }
        }

        public string DefaultPageExtension
        {
            get { return GetSafeValue<string>(SiteVariables.DefaultPageExtension); }
            set { this[SiteVariables.DefaultPageExtension] = value; }
        }

        public IEnumerable<FolderInfo> ContentDirectories
        {
            get
            {
                yield return BaseDirectory;

                foreach (var theme in Extends.CurrentList)
                {
                    yield return theme.Directory;
                }
            }
        }

        public string GetSafeDefaultPageExtension()
        {
            var extension = DefaultPageExtension;
            if (extension == ".html" || extension == ".htm")
            {
                return extension;
            }
            this.Warning($"Invalid [site.{SiteVariables.DefaultPageExtension} = \"{extension}\"]. Expecting only .html or htm. Reset to [{DefaultPageExtensionValue}]");
            DefaultPageExtension = DefaultPageExtensionValue;
            return DefaultPageExtensionValue;
        }

        public void Initialize()
        {
            Generator.Initialize();
        }

        public void Generate()
        {
            Generator.Run();
        }

        public void Load()
        {
            StaticFiles.Clear();
            Pages.Clear();

            // Get the list of root directories from themes
            var rootDirectories = new List<FolderInfo>(ContentDirectories);

            // Compute the list of files that we will actually process
            var filesLoaded = new HashSet<string>();
            foreach (var rootDirectory in rootDirectories)
            {
                var directories = new Queue<DirectoryInfo>();
                directories.Enqueue(rootDirectory);
                while (directories.Count > 0)
                {
                    var nextDirectory = directories.Dequeue();
                    LoadDirectory(rootDirectory, nextDirectory, directories, filesLoaded);
                }
            }

            // Sort pages by natural order
            Pages.Sort();
        }

        private void LoadPage(DirectoryInfo rootDirectory, FileInfo file, out ContentObject page)
        {
            page = null;
            var buffer = new byte[16];

            var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var count = stream.Read(buffer, 0, buffer.Length);
                // Rewind to 0
                stream.Position = 0;

                bool hasFrontMatter = false;
                bool isBinary = false;

                int startFrontMatter = 0;

                // Does it start with UTF8 BOM? If yes, skip it
                // EF BB BF
                if (buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                {
                    startFrontMatter = 3;
                }

                if (buffer[startFrontMatter] == '{' && buffer[startFrontMatter + 1] == '{')
                {
                    for (int i = startFrontMatter + 2; i < count; i++)
                    {
                        if (buffer[i] == 0)
                        {
                            isBinary = true;
                            break;
                        }
                    }

                    if (!isBinary)
                    {
                        hasFrontMatter = true;
                    }
                }

                if (hasFrontMatter)
                {
                    page = LoadPageScript(stream, rootDirectory, file);
                    stream = null;
                }
                else
                {
                    this.StaticFiles.Add(new ContentObject(this, rootDirectory, file));
                }
            }
            finally
            {
                // Dispose stream used
                stream?.Dispose();
            }
        }


        private ContentObject LoadPageScript(Stream stream, DirectoryInfo rootDirectory, FileInfo file)
        {
            // Read the stream
            var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            // Early dispose the stream
            stream.Dispose();

            ContentObject page = null;

            // Parse the page, using front-matter mode
            var scriptPage = Scripts.ParseScript(content, file.FullName, ScriptMode.FrontMatter);
            if (!scriptPage.HasErrors)
            {
                page = new ContentObject(this, rootDirectory, file)
                {
                    Script = scriptPage.Page
                };

                var evalClock = Stopwatch.StartNew();
                if (Generator.TryPreparePage(page))
                {
                    evalClock.Stop();

                    // Update statistics
                    var contentStat = Statistics.GetContentStat(page);
                    
                    contentStat.EvaluateTime += evalClock.Elapsed;

                    // Update the summary of the page
                    evalClock.Restart();
                    SummaryHelper.UpdateSummary(page);
                    evalClock.Stop();

                    // Update statistics
                    contentStat.SummaryTime += evalClock.Elapsed;
                }
            }

            return page;
        }

        private void LoadDirectory(FolderInfo rootDirectory, DirectoryInfo directory, Queue<DirectoryInfo> directoryQueue, HashSet<string> loaded)
        {
            var pages = new List<ContentObject>();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.Name == SiteFactory.DefaultConfigFilename)
                {
                    continue;
                }

                if (entry is FileInfo)
                {
                    // If the relative path is already registered, we won't process this file
                    var relativePath = rootDirectory.GetRelativePath(entry.FullName, PathFlags.Normalize);
                    if (loaded.Contains(relativePath))
                    {
                        continue;
                    }
                    loaded.Add(relativePath);

                    ContentObject page;
                    clock.Restart();
                    LoadPage(rootDirectory, (FileInfo)entry, out page);
                    clock.Stop();

                    if (page != null)
                    {
                        // Update statistics
                        Statistics.GetContentStat(page).LoadingParsingTime += clock.Elapsed;
                        Pages.Add(page);
                    }
                }
                else if (!entry.Name.StartsWith("_"))
                {
                    directoryQueue.Enqueue((DirectoryInfo)entry);
                }
            }
        }

        private class LoggerProviderIntercept : ILoggerProvider
        {
            private readonly SiteObject site;

            public LoggerProviderIntercept(SiteObject site)
            {
                this.site = site;
            }

            public void Dispose()
            {
            }

            public ILogger CreateLogger(string categoryName)
            {
                return new LoggerIntercept(site);
            }
        }

        private class LoggerIntercept : ILogger
        {
            private readonly SiteObject site;

            public LoggerIntercept(SiteObject site)
            {
                this.site = site;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (logLevel == LogLevel.Critical || logLevel == LogLevel.Error)
                {
                    site.HasErrors = true;
                }
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel == LogLevel.Critical || logLevel == LogLevel.Error;
            }

            public IDisposable BeginScope<TState>(TState state)
            {
                return null;
            }
        }
    }
}