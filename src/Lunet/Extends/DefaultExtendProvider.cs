// Copyright (c) Alexandre Mutel. All rights reserved.
// This file is licensed under the BSD-Clause 2 license. 
// See the license.txt file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Lunet.Core;
using Lunet.Helpers;
using Lunet.Scripts;
using Scriban.Runtime;

namespace Lunet.Extends
{
    public class DefaultExtendProvider : IExtendProvider
    {
        private bool loaded;
        private readonly List<ExtendDescription> cacheList;

        public DefaultExtendProvider()
        {
            RegistryUrl = "https://raw.githubusercontent.com/lunet-io/lunet-registry/master/extends.sban";
            cacheList = new List<ExtendDescription>();
        }

        public string Name => "lunet-registry/extends";

        public string RegistryUrl { get; set; }

        public IEnumerable<ExtendDescription> FindAll(SiteObject site)
        {
            if (loaded)
            {
                return cacheList;
            }

            if (RegistryUrl == null)
            {
                site.Error($"The registry Url for {nameof(DefaultExtendProvider)} cannot be null");
                return cacheList;
            }

            if (site.CanTrace())
            {
                site.Trace($"Checking remote registry [{RegistryUrl}] for available extensions/themes");
            }

            if (site == null) throw new ArgumentNullException(nameof(site));
            string themeRegistryStr;
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    themeRegistryStr = client.GetStringAsync(RegistryUrl).Result;
                }
            }
            catch (Exception ex)
            {
                site.Error($"Unable to load theme registry from Url [{RegistryUrl}]. Reason:{ex.GetReason()}");
                return cacheList;
            }

            var registryObject = new DynamicObject(this);
            if (site.Scripts.TryImportScript(themeRegistryStr, Name, registryObject, ScriptFlags.None))
            {
                var themeList = registryObject["extends"] as ScriptArray;
                if (themeList != null)
                {
                    foreach (var theme in themeList)
                    {
                        var themeObject = theme as ScriptObject;
                        if (themeObject != null)
                        {
                            var themeName = (string) themeObject["name"];
                            var themeDescription = (string) themeObject["description"];
                            var themeUrl = (string) themeObject["url"];
                            var themeDirectory = (string)themeObject["directory"];
                            cacheList.Add(new ExtendDescription(themeName, themeDescription, themeUrl, themeDirectory));
                        }
                    }
                }
            }

            loaded = true;
            return cacheList;
        }

        public bool TryInstall(SiteObject site, string extend, string version, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                version = "master";
            }

            foreach (var themeDesc in FindAll(site))
            {
                var fullVersion = themeDesc.Url + "/archive/" + version + ".zip";
                if (themeDesc.Name == extend)
                {
                    try
                    {
                        if (site.CanInfo())
                        {
                            site.Info($"Downloading and installing extension/theme [{extend}] to [{site.GetRelativePath(outputPath, PathFlags.Directory)}]");
                        }

                        using (HttpClient client = new HttpClient())
                        {
                            using (var stream = client.GetStreamAsync(fullVersion).Result)
                            {
                                site.Generator.CreateDirectory(new DirectoryInfo(outputPath));

                                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                                {
                                    var indexOfRepoName = themeDesc.Url.LastIndexOf('/');
                                    string repoName = themeDesc.Url.Substring(indexOfRepoName + 1);
                                    var directoryInZip = $"{repoName}-{version}/{themeDesc.Directory}";
                                    zip.ExtractToDirectory(outputPath, directoryInZip);
                                }
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        site.Error($"Unable to load extension/theme from Url [{fullVersion}]. Reason:{ex.GetReason()}");
                        break;
                    }
                }
            }
            return false;
        }
    }
}