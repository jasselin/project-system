﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Input;
using Microsoft.VisualStudio.ProjectSystem.VS.Editor;
using Moq;
using Xunit;

namespace Microsoft.VisualStudio.ProjectSystem.VS.Input.Commands
{
    [ProjectSystemTrait]
    public class EditProjectFileCommandTests
    {
        private const long CommandId = VisualStudioStandard2kCommandId.EditProjectFile;

        [Fact]
        public void NullAsUnconfiguredProject_Throws()
        {
            var editorPresenter = IProjectFileEditorPresenterFactory.CreateLazy();

            Assert.Throws<ArgumentNullException>("unconfiguredProject", () => new EditProjectFileCommand(null, editorPresenter));
        }

        [Fact]
        public void NullAsPresenter_Throws()
        {
            var unconfiguredProject = UnconfiguredProjectFactory.Create();

            Assert.Throws<ArgumentNullException>("editorPresenter", () => new EditProjectFileCommand(unconfiguredProject, null));
        }

        [Fact]
        public async Task RootNode_ShouldHandle()
        {
            var tree = ProjectTreeParser.Parse(@"
Root (flags: {ProjectRoot})
");

            var nodes = ImmutableHashSet.Create(tree);

            var unconfiguredProject = UnconfiguredProjectFactory.Create(filePath: @"C:\Temp\Root\Root.proj");

            var command = new EditProjectFileCommand(unconfiguredProject, IProjectFileEditorPresenterFactory.CreateLazy());

            var result = await command.GetCommandStatusAsync(nodes, CommandId, true, "", 0);
            Assert.True(result.Handled);
            Assert.Equal(CommandStatus.Enabled | CommandStatus.Supported, result.Status);
            Assert.Equal(string.Format(VSResources.EditProjectFileCommand, $"Root.proj"), result.CommandText);
        }

        [Fact]
        public async Task NonRootNode_ShouldHandle()
        {
            var tree = ProjectTreeParser.Parse(@"
Root (flags: {ProjectRoot})
    Properties ()
");

            var unconfiguredProject = UnconfiguredProjectFactory.Create(filePath: @"C:\Temp\Root\Root.proj");

            var nodes = ImmutableHashSet.Create(tree.Children[0]);

            var command = new EditProjectFileCommand(unconfiguredProject, IProjectFileEditorPresenterFactory.CreateLazy());

            var result = await command.GetCommandStatusAsync(nodes, CommandId, true, "", 0);
            Assert.True(result.Handled);
            Assert.Equal(CommandStatus.Enabled | CommandStatus.Supported, result.Status);
            Assert.Equal(string.Format(VSResources.EditProjectFileCommand, $"Root.proj"), result.CommandText);
        }

        [Fact]
        public async Task MultipleNodes_ShouldHandle()
        {
            var tree = ProjectTreeParser.Parse(@"
Root (flags: {ProjectRoot})
    Properties ()
");

            var unconfiguredProject = UnconfiguredProjectFactory.Create(filePath: @"C:\Temp\Root\Root.proj");

            var nodes = ImmutableHashSet.Create(tree, tree.Children[0]);

            var command = new EditProjectFileCommand(unconfiguredProject, IProjectFileEditorPresenterFactory.CreateLazy());

            var result = await command.GetCommandStatusAsync(nodes, CommandId, true, "", 0);
            Assert.True(result.Handled);
            Assert.Equal(CommandStatus.Enabled | CommandStatus.Supported, result.Status);
            Assert.Equal(string.Format(VSResources.EditProjectFileCommand, $"Root.proj"), result.CommandText);
        }

        [Fact]
        public async Task RootNode_CallsOpenAsync()
        {
            var tree = ProjectTreeParser.Parse(@"
Root (flags: {ProjectRoot})
");

            var nodes = ImmutableHashSet.Create(tree);

            var unconfiguredProject = UnconfiguredProjectFactory.Create(filePath: @"C:\Temp\Root\Root.proj");
            var editorStateModel = IProjectFileEditorPresenterFactory.Create();

            var command = new EditProjectFileCommand(unconfiguredProject, new Lazy<IProjectFileEditorPresenter>(() => editorStateModel));

            var result = await command.TryHandleCommandAsync(nodes, CommandId, true, 0, IntPtr.Zero, IntPtr.Zero);
            Assert.True(result);
            Mock.Get(editorStateModel).Verify(e => e.OpenEditorAsync(), Times.Once);
        }

        [Fact]
        public async Task NonRootNode_CallsOpenAsync()
        {
            var tree = ProjectTreeParser.Parse(@"
Root (flags: {ProjectRoot})
    Properties ()
");

            var unconfiguredProject = UnconfiguredProjectFactory.Create(filePath: @"C:\Temp\Root\Root.proj");

            var nodes = ImmutableHashSet.Create(tree.Children[0]);

            var editorStateModel = IProjectFileEditorPresenterFactory.Create();

            var command = new EditProjectFileCommand(unconfiguredProject, new Lazy<IProjectFileEditorPresenter>(() => editorStateModel));

            var result = await command.TryHandleCommandAsync(nodes, CommandId, true, 0, IntPtr.Zero, IntPtr.Zero);
            Assert.True(result);
            Mock.Get(editorStateModel).Verify(e => e.OpenEditorAsync(), Times.Once);
        }

        [Fact]
        public async Task MultipleNodes_CallsOpenAsync()
        {
            var tree = ProjectTreeParser.Parse(@"
Root (flags: {ProjectRoot})
    Properties ()
");

            var unconfiguredProject = UnconfiguredProjectFactory.Create(filePath: @"C:\Temp\Root\Root.proj");

            var nodes = ImmutableHashSet.Create(tree, tree.Children[0]);

            var editorStateModel = IProjectFileEditorPresenterFactory.Create();

            var command = new EditProjectFileCommand(unconfiguredProject, new Lazy<IProjectFileEditorPresenter>(() => editorStateModel));

            var result = await command.TryHandleCommandAsync(nodes, CommandId, true, 0, IntPtr.Zero, IntPtr.Zero);
            Assert.True(result);
            Mock.Get(editorStateModel).Verify(e => e.OpenEditorAsync(), Times.Once);
        }
    }
}
