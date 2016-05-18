// Copyright (c) Alexandre Mutel. All rights reserved.
// This file is licensed under the BSD-Clause 2 license. 
// See the license.txt file in the project root for more information.

using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Lunet.Core
{
    /// <summary>
    /// Manages the meta information associated to a site (from the `_meta` directory and `.lunet` directory)
    /// </summary>
    /// <seealso cref="ManagerBase" />
    public class MetaManager : ManagerBase
    {
        public const string MetaDirectoryName = "_meta";

        /// <summary>
        /// Initializes a new instance of the <see cref="MetaManager"/> class.
        /// </summary>
        /// <param name="site">The site.</param>
        public MetaManager(SiteObject site) : base(site)
        {
            Directory = Site.BaseDirectory.GetSubFolder(MetaDirectoryName); 
            PrivateDirectory = Site.PrivateBaseDirectory.GetSubFolder(MetaDirectoryName);
            BuiltinDirectory = Path.GetDirectoryName(typeof(MetaManager).GetTypeInfo().Assembly.Location);
        }

        public FolderInfo Directory { get; }

        public FolderInfo BuiltinDirectory { get; }


        public FolderInfo PrivateDirectory { get; }

        public IEnumerable<FolderInfo> Directories
        {
            get
            {
                yield return Directory;

                foreach (var theme in Site.Extends.CurrentList)
                {
                    yield return theme.Directory.GetSubFolder(MetaDirectoryName);
                }

                // Last one is the builtin directory
                yield return new DirectoryInfo(Path.Combine(BuiltinDirectory, MetaDirectoryName));
            }
        }
    }
}