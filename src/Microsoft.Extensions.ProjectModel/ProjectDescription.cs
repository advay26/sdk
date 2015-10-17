﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.ProjectModel.Graph;

namespace Microsoft.Extensions.ProjectModel
{
    public class ProjectDescription : LibraryDescription
    {
        // Create an unresolved project description
        public ProjectDescription(string name, string path)
            : base(
                  new LibraryRange(name, LibraryType.Unspecified),
                  new LibraryIdentity(name, LibraryType.Project),
                  path,
                  Enumerable.Empty<LibraryRange>(),
                  framework: null,
                  resolved: false,
                  compatible: false)
        { }

        public ProjectDescription(
            LibraryRange libraryRange,
            Project project,
            IEnumerable<LibraryRange> dependencies,
            TargetFrameworkInformation targetFrameworkInfo,
            bool resolved) :
                base(
                    libraryRange,
                    new LibraryIdentity(project.Name, project.Version, LibraryType.Project),
                    project.ProjectFilePath,
                    dependencies,
                    targetFrameworkInfo.FrameworkName,
                    resolved,
                    compatible: true)
        {
            Project = project;
            TargetFrameworkInfo = targetFrameworkInfo;
        }

        public Project Project { get; }

        public TargetFrameworkInformation TargetFrameworkInfo { get; }
    }
}
