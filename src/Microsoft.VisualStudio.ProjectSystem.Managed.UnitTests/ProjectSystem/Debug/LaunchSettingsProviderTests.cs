﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

#pragma warning disable CS0618 // Type or member is obsolete

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.IO;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.ProjectSystem.SpecialFileProviders;
using Microsoft.VisualStudio.Threading.Tasks;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.VisualStudio.ProjectSystem.Debug
{
    [ProjectSystemTrait]
    public class LaunchSettingsProviderTests
    {
        internal LaunchSettingsUnderTest GetLaunchSettingsProvider(IFileSystem fileSystem, string appDesignerFolder = @"c:\test\Project1\Properties", string activeProfile = "")
        {
            Mock<IEnumValue> activeProfileValue = new Mock<IEnumValue>();
            activeProfileValue.Setup(s => s.Name).Returns(activeProfile);
            var debuggerData = new PropertyPageData() {
                Category = ProjectDebugger.SchemaName,
                PropertyName = ProjectDebugger.ActiveDebugProfileProperty,
                Value = activeProfileValue.Object
            };

            var specialFilesManager = ISpecialFilesManagerFactory.ImplementGetFile(appDesignerFolder);
            var unconfiguredProject = UnconfiguredProjectFactory.Create(null, null, @"c:\test\Project1\Project1.csproj");
            var properties = ProjectPropertiesFactory.Create(unconfiguredProject, new[] { debuggerData  });
            var commonServices = IUnconfiguredProjectCommonServicesFactory.Create(unconfiguredProject, null,  new IProjectThreadingServiceMock(), null, properties);
            var projectServices = IUnconfiguredProjectServicesFactory.Create(IProjectAsynchronousTasksServiceFactory.Create(CancellationToken.None));
            var provider = new LaunchSettingsUnderTest(unconfiguredProject, projectServices, fileSystem ?? new IFileSystemMock(), commonServices, null, specialFilesManager);
            return provider;
        }

        internal void SetJsonSerializationProviders(LaunchSettingsUnderTest provider)
        {
            var mockMetadata = new Mock<IOrderPrecedenceMetadataView>();
            var mockIJsonSection = new Mock<IJsonSection>();
            mockIJsonSection.Setup(s => s.JsonSection).Returns("iisSettings");
            mockIJsonSection.Setup(s => s.SerializationType).Returns(typeof(IISSettingsData));
            var lazyProvider = new Lazy<ILaunchSettingsSerializationProvider, IJsonSection>(() =>
            {
                var mockSerializer = new Mock<ILaunchSettingsSerializationProvider>();
                return mockSerializer.Object;
            }, mockIJsonSection.Object, true);
            var settingsProviders = new OrderPrecedenceImportCollection<ILaunchSettingsSerializationProvider, IJsonSection>(ImportOrderPrecedenceComparer.PreferenceOrder.PreferredComesFirst, (UnconfiguredProject)null);
            settingsProviders.Add(new Lazy<ILaunchSettingsSerializationProvider, IJsonSection>(() => lazyProvider.Value, mockIJsonSection.Object));
            provider.SetSettingsProviderCollection(settingsProviders);

        }

        [Fact]
        public void WhenNoAppDesignerFolder_LaunchSettingsIsInRoot()
        {
            var provider = GetLaunchSettingsProvider(null, appDesignerFolder: null);

            Assert.Equal(@"c:\test\Project1\launchSettings.json", provider.LaunchSettingsFile);
        }

        [Theory]
        [InlineData(@"C:\Properties",                @"C:\Properties\launchSettings.json")]
        [InlineData(@"C:\Project\Properties",        @"C:\Project\Properties\launchSettings.json")]
        [InlineData(@"C:\Project\My Project",        @"C:\Project\My Project\launchSettings.json")]
        public async Task WhenAppDesignerFolder_LaunchSettingsIsInAppDesignerFolder(string appDesignerFolder, string expected)
        {
            var provider = GetLaunchSettingsProvider(null, appDesignerFolder: appDesignerFolder);

            string result = await provider.GetLaunchSettingsFilePathNoCacheAsync();

            Assert.Equal(expected, result);
        }

        [Fact]
        public void ActiveProfileTests()
        { 
            string activeProfile = "MyCommand";
            var testProfiles = new Mock<ILaunchSettings>();
            testProfiles.Setup(m => m.ActiveProfile).Returns(new LaunchProfile() { Name = activeProfile });
            var provider = GetLaunchSettingsProvider(null);
            Assert.Equal(null, provider.ActiveProfile);

            provider.SetCurrentSnapshot(testProfiles.Object);
            Assert.Equal(activeProfile, provider.ActiveProfile.Name);
        }

        [Fact]
        public async Task UpdateProfiles_NoSettingsFile()
        {

            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            // No settings  file, should add the default profile
            await provider.UpdateProfilesAsyncTest(null);
            Assert.Equal(1, provider.CurrentSnapshot.Profiles.Count);
            Assert.Equal("Project", provider.CurrentSnapshot.ActiveProfile.CommandName);
        }

        [Fact]
        public async Task UpdateProfilesBasicSettingsFile()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);
            moqFS.WriteAllText(provider.LaunchSettingsFile, JsonString1);

            await provider.UpdateProfilesAsyncTest(null);
            Assert.Equal(4, provider.CurrentSnapshot.Profiles.Count);
            Assert.Equal(0, provider.CurrentSnapshot.GlobalSettings.Count);
            Assert.Equal("IIS Express", provider.CurrentSnapshot.ActiveProfile.Name);
        }

        [Fact]
        public async Task UpdateProfilesSetActiveProfileFromProperty()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);
            moqFS.WriteAllText(provider.LaunchSettingsFile, JsonString1);

            // Change the value of activeDebugProfile to web it should be the active one. Similates a change
            // on disk doesn't affect active profile
            provider = GetLaunchSettingsProvider(moqFS, activeProfile: "web");
            await provider.UpdateProfilesAsyncTest(null);
            Assert.Equal("web", provider.CurrentSnapshot.ActiveProfile.Name);
        }

        [Fact]
        public async Task UpdateProfiles_ChangeActiveProfileOnly()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);
            moqFS.WriteAllText(provider.LaunchSettingsFile, JsonString1);
            await provider.UpdateProfilesAsyncTest(null);

            // don't change file on disk, just active one
            await provider.UpdateProfilesAsyncTest("Docker");
            Assert.Equal(4, provider.CurrentSnapshot.Profiles.Count);
            Assert.Equal(0, provider.CurrentSnapshot.GlobalSettings.Count);
            Assert.Equal("Docker", provider.CurrentSnapshot.ActiveProfile.Name);
        }

        [Fact]
        public async Task UpdateProfiles_BadJsonShouldLeaveProfilesStable()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);
            moqFS.WriteAllText(provider.LaunchSettingsFile, JsonString1);
            await provider.UpdateProfilesAsyncTest(null);

            moqFS.WriteAllText(provider.LaunchSettingsFile, BadJsonString);
            await provider.UpdateProfilesAsyncTest("Docker");
            Assert.Equal(4, provider.CurrentSnapshot.Profiles.Count);
            Assert.Equal(0, provider.CurrentSnapshot.GlobalSettings.Count);
            Assert.Equal("IIS Express", provider.CurrentSnapshot.ActiveProfile.Name);
        }
            
        [Fact]
        public async Task UpdateProfiles_SetsErrorProfileTests()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);
            moqFS.WriteAllText(provider.LaunchSettingsFile, BadJsonString);

            await provider.UpdateProfilesAsyncTest("Docker");
            Assert.Equal(1, provider.CurrentSnapshot.Profiles.Count);
            Assert.Equal(LaunchSettingsProvider.ErrorProfileCommandName, provider.CurrentSnapshot.ActiveProfile.CommandName);
        }

        [Fact]
        public async Task SettingsFileHasChangedTests()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            // No settings  file
            Assert.True(await provider.SettingsFileHasChangedAsyncTest());
            moqFS.WriteAllText(provider.LaunchSettingsFile, JsonString1);

            Assert.True(await provider.SettingsFileHasChangedAsyncTest());
            provider.LastSettingsFileSyncTimeTest = moqFS.LastFileWriteTime(provider.LaunchSettingsFile);
            Assert.False(await provider.SettingsFileHasChangedAsyncTest());
        }


        [Fact]
        public async Task ReadProfilesFromDisk_NoFile()
        {

            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            // Test without an existing file. Should throw
            LaunchSettingsData launchSettings;
            try
            {
                launchSettings = await provider.ReadSettingsFileFromDiskTestAsync();
                Assert.True(false);
            }
            catch
            {   // Should have logged an error

            }
        }

        [Fact]
        public async Task ReadProfilesFromDisk_GoodFile()
        {

            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            // write a good file
            moqFS.WriteAllText(provider.LaunchSettingsFile, JsonString1);

            var launchSettings = await provider.ReadSettingsFileFromDiskTestAsync();
            Assert.Equal(4, launchSettings.Profiles.Count);
        }

        [Fact]
        public async Task ReadProfilesFromDisk_BadJsonFile()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            moqFS.WriteAllText(provider.LaunchSettingsFile, BadJsonString);
            try
            {
                var launchSettings = await provider.ReadSettingsFileFromDiskTestAsync();
                Assert.True(false);
            }
            catch
            {   // Should have logged an error
            }
        }

        [Fact]
        public async Task ReadProfilesFromDisk_JsonWithExtensionsNoProvider()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            // Write a json file containing extension settings
            moqFS.WriteAllText(provider.LaunchSettingsFile, JsonStringWithWebSettings);
            var launchSettings = await provider.ReadSettingsFileFromDiskTestAsync();
            Assert.Equal(2, launchSettings.Profiles.Count);
            Assert.Equal(1, launchSettings.OtherSettings.Count);
            Assert.True(launchSettings.OtherSettings["iisSettings"] is JObject);
        }

        [Fact]
        public async Task ReadProfilesFromDisk_JsonWithExtensionsWithProvider()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            // Write a json file containing extension settings
            moqFS.WriteAllText(provider.LaunchSettingsFile, JsonStringWithWebSettings);
        
            // Set the serialization provider
            SetJsonSerializationProviders(provider);

            var launchSettings = await provider.ReadSettingsFileFromDiskTestAsync();
            Assert.Equal(2, launchSettings.Profiles.Count);
            Assert.Equal(1, launchSettings.OtherSettings.Count);
            Assert.True(launchSettings.OtherSettings["iisSettings"] is IISSettingsData);
        }

        [Fact]
        public async Task SaveProfilesToDiskTests()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            var profiles = new List<ILaunchProfile>()
            {
                { new LaunchProfile() { Name = "IIS Express", CommandName="IISExpress", LaunchBrowser=true } },
                {new LaunchProfile() { Name = "bar", ExecutablePath="c:\\test\\project\\bin\\test.exe", CommandLineArgs=@"-someArg"} }
            };

            var testSettings = new Mock<ILaunchSettings>();
            testSettings.Setup(m => m.ActiveProfile).Returns(() => {return profiles[0];});
            testSettings.Setup(m => m.Profiles).Returns(() =>
            {
                return profiles.ToImmutableList();
            });
            testSettings.Setup(m => m.GlobalSettings).Returns(() =>
            {
                IISSettingsData iisSettings = new IISSettingsData()
                {
                    AnonymousAuthentication = false,
                    WindowsAuthentication = true,
                    IISExpressBindingData = new ServerBindingData()
                    {
                        ApplicationUrl = "http://localhost:12345/",
                        SSLPort = 44301
                    }
                };
                return ImmutableDictionary<string, object>.Empty.Add("iisSettings", iisSettings);
            });

            await provider.SaveSettingsToDiskAsyncTest(testSettings.Object);

            // Last Write time should be set
            Assert.Equal(moqFS.LastFileWriteTime(provider.LaunchSettingsFile), provider.LastSettingsFileSyncTimeTest);

            // Check disk contents
            Assert.Equal(JsonStringWithWebSettings, moqFS.ReadAllText(provider.LaunchSettingsFile), ignoreLineEndingDifferences:true);
        }

        [Fact]
        public async Task LaunchSettingsFile_Changed()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            // Write file and generate disk change
            var eventArgs = new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(provider.LaunchSettingsFile), Path.GetFileName(provider.LaunchSettingsFile));
            moqFS.WriteAllText(provider.LaunchSettingsFile, JsonString1);
            provider.LaunchSettingsFile_ChangedTest(eventArgs);

            // Wait for completion of task
            await provider.FileChangeScheduler.LatestScheduledTask;
            Assert.NotNull(provider.CurrentSnapshot);
            Assert.Equal(4, provider.CurrentSnapshot.Profiles.Count);
        }

        [Fact]
        public async Task LaunchSettingsFile_TestIgnoreFlag()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            string fileName = await provider.GetLaunchSettingsFilePathNoCacheAsync();
            // Write file and generate disk change
            var eventArgs = new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(fileName), Path.GetFileName(fileName));
            moqFS.WriteAllText(fileName, JsonString1);

            // Set the ignore flag. It should be ignored.
            provider.LastSettingsFileSyncTimeTest = DateTime.MinValue;
            provider.SetIgnoreFileChanges(true);
            provider.LaunchSettingsFile_ChangedTest(eventArgs);
            Assert.Null(provider.FileChangeScheduler.LatestScheduledTask);
            Assert.Null(provider.CurrentSnapshot);

            // Should run this time
            provider.SetIgnoreFileChanges(false);
            provider.LaunchSettingsFile_ChangedTest(eventArgs);
            await provider.FileChangeScheduler.LatestScheduledTask;
            Assert.NotNull(provider.CurrentSnapshot);
            Assert.Equal(4, provider.CurrentSnapshot.Profiles.Count);
        }

        [Fact]
        public async Task LaunchSettingsFile_TestTimeStampFlag()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            // Write file and generate disk change
            var eventArgs = new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(provider.LaunchSettingsFile), Path.GetFileName(provider.LaunchSettingsFile));
            moqFS.WriteAllText(provider.LaunchSettingsFile, JsonString1);
            provider.LaunchSettingsFile_ChangedTest(eventArgs);
            await provider.FileChangeScheduler.LatestScheduledTask;
            Assert.Equal(4, provider.CurrentSnapshot.Profiles.Count);

            // Write new file, but set the timestamp to match
            moqFS.WriteAllText(provider.LaunchSettingsFile, JsonStringWithWebSettings);
            provider.LastSettingsFileSyncTimeTest = moqFS.LastFileWriteTime(provider.LaunchSettingsFile);
            provider.LaunchSettingsFile_ChangedTest(eventArgs);
            await provider.FileChangeScheduler.LatestScheduledTask;
            Assert.Equal(4, provider.CurrentSnapshot.Profiles.Count);

            moqFS.WriteAllText(provider.LaunchSettingsFile, JsonStringWithWebSettings);
            provider.LaunchSettingsFile_ChangedTest(eventArgs);
            await provider.FileChangeScheduler.LatestScheduledTask;
            Assert.Equal(2, provider.CurrentSnapshot.Profiles.Count);
        }
        
        [Fact]
        public void DisposeTests()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);
            Assert.False(provider.DisposeObjectsAreNull());
            provider.CallDispose();
            Assert.True(provider.DisposeObjectsAreNull());
           
        }

        [Fact]
        public async Task UpdateAndSaveProfilesAsync()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            var profiles = new List<ILaunchProfile>()
            {
                {new LaunchProfile() { Name = "IIS Express", CommandName="IISExpress", LaunchBrowser=true } },
                {new LaunchProfile() { Name = "bar", ExecutablePath ="c:\\test\\project\\bin\\test.exe", CommandLineArgs=@"-someArg"} }
            };

            var testSettings = new Mock<ILaunchSettings>();
            testSettings.Setup(m => m.ActiveProfile).Returns(() => {return profiles[0];});
            testSettings.Setup(m => m.Profiles).Returns(() =>
            {
                return profiles.ToImmutableList();
            });

            testSettings.Setup(m => m.GlobalSettings).Returns(() =>
            {
                IISSettingsData iisSettings = new IISSettingsData()
                {
                    AnonymousAuthentication = false,
                    WindowsAuthentication = true,
                    IISExpressBindingData = new ServerBindingData()
                    {
                        ApplicationUrl = "http://localhost:12345/",
                        SSLPort = 44301
                    }
                };
                return ImmutableDictionary<string, object>.Empty.Add("iisSettings", iisSettings);
            });

            await provider.UpdateAndSaveSettingsAsync(testSettings.Object).ConfigureAwait(true);

            // Check disk contents
            Assert.Equal(JsonStringWithWebSettings, moqFS.ReadAllText(provider.LaunchSettingsFile), ignoreLineEndingDifferences:true);

            // Check snapshot
            Assert.Equal(2, provider.CurrentSnapshot.Profiles.Count);
            Assert.Equal(1, provider.CurrentSnapshot.GlobalSettings.Count);

            // Verify the activeProfile is set to the first one since no existing snapshot
            Assert.Equal("IIS Express", provider.CurrentSnapshot.ActiveProfile.Name);
        }

        [Fact]
        public async Task UpdateAndSaveProfilesAsync_ActiveProfilePreserved()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS, "Properties", "bar");

            var existingSettings = new Mock<ILaunchSettings>();
            existingSettings.Setup(m => m.ActiveProfile).Returns(new LaunchProfile() {Name= "bar"});
            provider.SetCurrentSnapshot(existingSettings.Object);
            var profiles = new List<ILaunchProfile>()
            {
                {new LaunchProfile() { Name = "IIS Express", CommandName="IISExpress", LaunchBrowser=true } },
                {new LaunchProfile() { Name = "bar", ExecutablePath ="c:\\test\\project\\bin\\test.exe", CommandLineArgs=@"-someArg"} }
            };

            var testSettings = new Mock<ILaunchSettings>();
            testSettings.Setup(m => m.ActiveProfile).Returns(() => {return profiles[0];});
            testSettings.Setup(m => m.Profiles).Returns(() =>
            {
                return profiles.ToImmutableList();
            });

            testSettings.Setup(m => m.GlobalSettings).Returns(() => ImmutableDictionary<string, object>.Empty);

            await provider.UpdateAndSaveSettingsAsync(testSettings.Object).ConfigureAwait(true);

            // Verify the activeProfile hasn't changed
            Assert.Equal("bar", provider.CurrentSnapshot.ActiveProfile.Name);
        }

        [Theory]
        [InlineData(true, 0)]
        [InlineData(false, 2)]
        public async Task AddOrUpdateProfileAsync_ProfileDoesntExist(bool addToFront, int expectedIndex)
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            var profiles = new List<ILaunchProfile>()
            {
                {new LaunchProfile() { Name = "IIS Express", CommandName="IISExpress", LaunchBrowser=true } },
                {new LaunchProfile() { Name = "bar", ExecutablePath ="c:\\test\\project\\bin\\test.exe", CommandLineArgs=@"-someArg"} }
            };

            var testSettings = new Mock<ILaunchSettings>();
            testSettings.Setup(m => m.Profiles).Returns(profiles.ToImmutableList());

            provider.SetCurrentSnapshot(testSettings.Object);

            var newProfile = new LaunchProfile() { Name = "test", CommandName = "Test" };

            await provider.AddOrUpdateProfileAsync(newProfile, addToFront).ConfigureAwait(true);

            // Check disk file was written
            Assert.True(moqFS.FileExists(provider.LaunchSettingsFile));

            // Check snapshot
            Assert.Equal(3, provider.CurrentSnapshot.Profiles.Count);
            Assert.Equal("Test", provider.CurrentSnapshot.Profiles[expectedIndex].CommandName);
            Assert.Null(provider.CurrentSnapshot.Profiles[expectedIndex].ExecutablePath);
        }

        [Theory]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        public async Task AddOrUpdateProfileAsync_ProfileExists(bool addToFront, int expectedIndex)
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            var profiles = new List<ILaunchProfile>()
            {
                {new LaunchProfile() { Name = "IIS Express", CommandName="IISExpress", LaunchBrowser=true } },
                {new LaunchProfile() { Name = "test", ExecutablePath ="c:\\test\\project\\bin\\test.exe", CommandLineArgs=@"-someArg"} },
                {new LaunchProfile() { Name = "bar", ExecutablePath ="c:\\test\\project\\bin\\bar.exe"} }
            };

            var testSettings = new Mock<ILaunchSettings>();
            testSettings.Setup(m => m.Profiles).Returns(profiles.ToImmutableList());

            provider.SetCurrentSnapshot(testSettings.Object);

            var newProfile = new LaunchProfile() { Name = "test", CommandName = "Test" };

            await provider.AddOrUpdateProfileAsync(newProfile, addToFront).ConfigureAwait(true);

            // Check disk file was written
            Assert.True(moqFS.FileExists(provider.LaunchSettingsFile));

            // Check snapshot
            Assert.Equal(3, provider.CurrentSnapshot.Profiles.Count);
            Assert.Equal("test", provider.CurrentSnapshot.Profiles[expectedIndex].Name);
            Assert.Equal("Test", provider.CurrentSnapshot.Profiles[expectedIndex].CommandName);
            Assert.Null(provider.CurrentSnapshot.Profiles[expectedIndex].ExecutablePath);
        }

        [Fact]
        public async Task RemoveProfileAsync_ProfileExists()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            var profiles = new List<ILaunchProfile>()
            {
                {new LaunchProfile() { Name = "IIS Express", CommandName="IISExpress", LaunchBrowser=true } },
                {new LaunchProfile() { Name = "test", ExecutablePath ="c:\\test\\project\\bin\\test.exe", CommandLineArgs=@"-someArg"} },
                {new LaunchProfile() { Name = "bar", ExecutablePath ="c:\\test\\project\\bin\\bar.exe"} }
            };

            var testSettings = new Mock<ILaunchSettings>();
            testSettings.Setup(m => m.Profiles).Returns(profiles.ToImmutableList());

            provider.SetCurrentSnapshot(testSettings.Object);

            await provider.RemoveProfileAsync("test").ConfigureAwait(true);

            // Check disk file was written
            Assert.True(moqFS.FileExists(provider.LaunchSettingsFile));

            // Check snapshot
            Assert.Equal(2, provider.CurrentSnapshot.Profiles.Count);
            Assert.Null(provider.CurrentSnapshot.Profiles.FirstOrDefault(p => p.Name.Equals("test")));
        }

        [Fact]
        public async Task RemoveProfileAsync_ProfileDoesntExists()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            var profiles = new List<ILaunchProfile>()
            {
                {new LaunchProfile() { Name = "IIS Express", CommandName="IISExpress", LaunchBrowser=true } },
                {new LaunchProfile() { Name = "bar", ExecutablePath ="c:\\test\\project\\bin\\bar.exe"} }
            };

            var testSettings = new Mock<ILaunchSettings>();
            testSettings.Setup(m => m.Profiles).Returns(profiles.ToImmutableList());

            provider.SetCurrentSnapshot(testSettings.Object);

            await provider.RemoveProfileAsync("test").ConfigureAwait(true);

            // Check disk file was not written
            Assert.False(moqFS.FileExists(provider.LaunchSettingsFile));

            // Check snapshot
            Assert.Equal(2, provider.CurrentSnapshot.Profiles.Count);
        }

        [Fact]
        public async Task AddOrUpdateGlobalSettingAsync_SettingDoesntExist()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            // Set the serialization provider
            SetJsonSerializationProviders(provider);

            var globalSettings = ImmutableDictionary<string, object>.Empty.Add("test", new LaunchProfile());

            var testSettings = new Mock<ILaunchSettings>();
            testSettings.Setup(m => m.GlobalSettings).Returns(globalSettings);
            testSettings.Setup(m => m.Profiles).Returns(ImmutableList<ILaunchProfile>.Empty);

            provider.SetCurrentSnapshot(testSettings.Object);

            var newSettings = new IISSettingsData() { WindowsAuthentication = true };

            await provider.AddOrUpdateGlobalSettingAsync("iisSettings", newSettings).ConfigureAwait(true);

            // Check disk file was written
            Assert.True(moqFS.FileExists(provider.LaunchSettingsFile));
            Assert.Equal(2, provider.CurrentSnapshot.GlobalSettings.Count);
            // Check snapshot
            Assert.True(provider.CurrentSnapshot.GlobalSettings.TryGetValue("iisSettings", out object updatedSettings));
            Assert.True(((IISSettingsData)updatedSettings).WindowsAuthentication);
        }

        [Fact]
        public async Task AddOrUpdateGlobalSettingAsync_SettingExists()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            // Set the serialization provider
            SetJsonSerializationProviders(provider);

            var globalSettings = ImmutableDictionary<string, object>.Empty
                                                    .Add("test", new LaunchProfile())
                                                    .Add("iisSettings", new IISSettingsData()); 

            var testSettings = new Mock<ILaunchSettings>();
            testSettings.Setup(m => m.GlobalSettings).Returns(globalSettings);
            testSettings.Setup(m => m.Profiles).Returns(ImmutableList<ILaunchProfile>.Empty);

            provider.SetCurrentSnapshot(testSettings.Object);

            var newSettings = new IISSettingsData() { WindowsAuthentication = true };

            await provider.AddOrUpdateGlobalSettingAsync("iisSettings", newSettings).ConfigureAwait(true);

            // Check disk file was written
            Assert.True(moqFS.FileExists(provider.LaunchSettingsFile));
            // Check snapshot
            Assert.Equal(2, provider.CurrentSnapshot.GlobalSettings.Count);
            Assert.True(provider.CurrentSnapshot.GlobalSettings.TryGetValue("iisSettings", out object updatedSettings));
            Assert.True(((IISSettingsData)updatedSettings).WindowsAuthentication);
        }
        [Fact]
        public async Task RemoveGlobalSettingAsync_SettingDoesntExist()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            // Set the serialization provider
            SetJsonSerializationProviders(provider);

            var globalSettings = ImmutableDictionary<string, object>.Empty.Add("test", new LaunchProfile());

            var testSettings = new Mock<ILaunchSettings>();
            testSettings.Setup(m => m.GlobalSettings).Returns(globalSettings);
            testSettings.Setup(m => m.Profiles).Returns(ImmutableList<ILaunchProfile>.Empty);

            provider.SetCurrentSnapshot(testSettings.Object);

            await provider.RemoveGlobalSettingAsync("iisSettings").ConfigureAwait(true);

            // Check disk file was not written
            Assert.False(moqFS.FileExists(provider.LaunchSettingsFile));

            // Check snapshot
            Assert.Equal(1, provider.CurrentSnapshot.GlobalSettings.Count);
        }

        [Fact]
        public async Task RemoveGlobalSettingAsync_SettingExists()
        {
            IFileSystemMock moqFS = new IFileSystemMock();
            var provider = GetLaunchSettingsProvider(moqFS);

            // Set the serialization provider
            SetJsonSerializationProviders(provider);

            var globalSettings = ImmutableDictionary<string, object>.Empty
                                                    .Add("test", new LaunchProfile())
                                                    .Add("iisSettings", new IISSettingsData()); 

            var testSettings = new Mock<ILaunchSettings>();
            testSettings.Setup(m => m.GlobalSettings).Returns(globalSettings);
            testSettings.Setup(m => m.Profiles).Returns(ImmutableList<ILaunchProfile>.Empty);

            provider.SetCurrentSnapshot(testSettings.Object);

            await provider.RemoveGlobalSettingAsync("iisSettings").ConfigureAwait(true);

            // Check disk file was written
            Assert.True(moqFS.FileExists(provider.LaunchSettingsFile));

            // Check snapshot
            Assert.Equal(1, provider.CurrentSnapshot.GlobalSettings.Count);
            Assert.False(provider.CurrentSnapshot.GlobalSettings.TryGetValue("iisSettings", out object updatedSettings));
        }

string JsonString1 = @"{
  ""profiles"": {
  ""IIS Express"":
    {
      ""commandName"": ""IISExpress"",
      ""launchUrl"": ""http://localhost:1234:/test.html"",
      ""launchBrowser"": true
    },
    ""HasCustomValues"":
    {
      ""executablePath"": ""c:\\test\\project\\bin\\project.exe"",
      ""workingDirectory"": ""c:\\test\\project"",
      ""commandLineArgs"": ""--arg1 --arg2"",
      ""custom1"": true,
      ""custom2"": 124,
      ""custom3"": ""mycustomVal""
    },
    ""Docker"":
    {
      ""commandName"": ""Docker"",
      ""launchBrowser"": false,
      ""dockerOption1"": ""some option in docker"",
      ""dockerOption2"": ""Another option in docker""
    },
    ""web"":
    {
      ""commandName"": ""Project"",
      ""launchBrowser"": true,
      ""environmentVariables"": {
        ""ASPNET_ENVIRONMENT"": ""Development"",
        ""ASPNET_APPLICATIONBASE"": ""c:\\Users\\billhie\\Documents\\projects\\WebApplication8\\src\\WebApplication8""
      }
    }
  }
}";

        string JsonStringWithWebSettings = @"{
  ""iisSettings"": {
    ""windowsAuthentication"": true,
    ""anonymousAuthentication"": false,
    ""iisExpress"": {
      ""applicationUrl"": ""http://localhost:12345/"",
      ""sslPort"": 44301
    }
  },
  ""profiles"": {
    ""IIS Express"": {
      ""commandName"": ""IISExpress"",
      ""launchBrowser"": true
    },
    ""bar"": {
      ""executablePath"": ""c:\\test\\project\\bin\\test.exe"",
      ""commandLineArgs"": ""-someArg""
    }
  }
}";

        string BadJsonString = @"{
  ""profiles"": {
    {
      ""name"": ""IIS Express"",
      ""launchBrowser"": ""True""
    },
    },
    {
      ""Name"": ""bar"",
      ""launchBrowser"": ""False""
    }
  }
}";
    }

    // Dervies from base class to be able to set protected memebers
    internal class LaunchSettingsUnderTest : LaunchSettingsProvider
    {
        // ECan pass null for all and a default will be crewated
        public LaunchSettingsUnderTest(UnconfiguredProject unconfiguredProject, IUnconfiguredProjectServices projectServices, 
                                      IFileSystem fileSystem,   IUnconfiguredProjectCommonServices commonProjectServices, 
                                      IActiveConfiguredProjectSubscriptionService projectSubscriptionService, ISpecialFilesManager specialFilesManager)
          : base(unconfiguredProject, projectServices, fileSystem, commonProjectServices, projectSubscriptionService, specialFilesManager)
        {
            // Block the code from setting up one on the real file system. Since we block, it we need to set up the fileChange scheduler manually
            FileWatcher = new SimpleFileWatcher();
            // Make the unit tests run faster
            FileChangeProcessingDelay = TimeSpan.FromMilliseconds(50);
            FileChangeScheduler = new TaskDelayScheduler(FileChangeProcessingDelay, commonProjectServices.ThreadingService,
                    CancellationToken.None);
        }

        // Wrappers to call protected members
        public void SetCurrentSnapshot(ILaunchSettings profiles) { CurrentSnapshot = profiles;}
        public Task<LaunchSettingsData> ReadSettingsFileFromDiskTestAsync() { return ReadSettingsFileFromDiskAsync();}
        public Task SaveSettingsToDiskAsyncTest(ILaunchSettings curSettings) { return SaveSettingsToDiskAsync(curSettings);}
        public DateTime LastSettingsFileSyncTimeTest { get { return LastSettingsFileSyncTime; } set { LastSettingsFileSyncTime = value; } }
        public Task UpdateProfilesAsyncTest(string activeProfile) { return UpdateProfilesAsync(activeProfile);}
        public void SetIgnoreFileChanges(bool value) { IgnoreFileChanges = value; }
        public Task<bool> SettingsFileHasChangedAsyncTest() { return SettingsFileHasChangedAsync(); }
        public void LaunchSettingsFile_ChangedTest(FileSystemEventArgs args)
        {
            LaunchSettingsFile_Changed(null, args);
        }
        public void CallDispose() {Dispose(true);}
        public bool DisposeObjectsAreNull()
        {
            return FileChangeScheduler == null &&
                   FileWatcher == null &&
                   ProjectRuleSubscriptionLink == null && 
                   _broadcastBlock == null;
        }

        internal void SetSettingsProviderCollection(OrderPrecedenceImportCollection<ILaunchSettingsSerializationProvider, IJsonSection> settingsProviders)
        {
            JsonSerializationProviders = settingsProviders;
        }
    }

    // Used to test global settings
    [JsonObject(MemberSerialization.OptIn)]
    internal class ServerBindingData
    {
        [JsonProperty(PropertyName = "applicationUrl")]
        public string ApplicationUrl { get; set; }

        [JsonProperty(PropertyName = "sslPort")]
        public int SSLPort { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal class IISSettingsData
    {
        public const bool DefaultAnonymousAuth = true;
        public const bool DefaultWindowsAuth = false;

        [JsonProperty(PropertyName = "windowsAuthentication")]
        public bool WindowsAuthentication { get; set; } = DefaultWindowsAuth;

        [JsonProperty(PropertyName = "anonymousAuthentication")]
        public bool AnonymousAuthentication { get; set; } = DefaultAnonymousAuth;

        [JsonProperty(PropertyName = "iis")]
        public ServerBindingData IISBindingData { get; set; }


        [JsonProperty(PropertyName = "iisExpress")]
        public ServerBindingData IISExpressBindingData { get; set; }
    }
}
