﻿// Copyright (c) Alexandre Mutel. All rights reserved.
// This file is licensed under the BSD-Clause 2 license. 
// See the license.txt file in the project root for more information.

using System.Collections.Generic;

namespace Lunet.Core
{
    public interface IContentProvider
    {
        IEnumerable<FolderInfo> GetFolders();
    }
}