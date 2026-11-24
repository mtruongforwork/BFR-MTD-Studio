namespace BFR.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BFR.Core.WatchFolder;
using Xunit;

public class StabilityAndTokensV22Tests
{
    // -----------------------------------------------------------------
    // STAB-5: WatchFolderManager rollback & WatchRule.WorkflowFilePath
    // -----------------------------------------------------------------

    [Fact]
    public void WatchRuleStore_PersistsWorkflowFilePath()
    {
        var fs = new FakeFileSystem();
        var store = new WatchRuleStore(fs, @"C:\cfg\watch.json");
        var rule = new WatchRule
        {
            Name = "Inbox",
            Folder = @"C:\Inbox",
            WorkflowFilePath = @"C:\Workflows\photos.bfrw.json",
            Steps = new List<WorkflowStep> { new RenameStep(new Preset { Name = "p" }) }
        };

        store.Save(new[] { rule });
        var loaded = store.Load();

        Assert.Single(loaded);
        Assert.Equal(@"C:\Workflows\photos.bfrw.json", loaded[0].WorkflowFilePath);
    }

    [Fact]
    public void WatchFolderManager_Start_StopsEarlierRulesWhenLaterRuleFails()
    {
        var existingDir = Path.Combine(Path.GetTempPath(), "bfr-wfm-" + Guid.NewGuid().ToString("N"));
        var missingDir = Path.Combine(Path.GetTempPath(), "bfr-wfm-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(existingDir);

        try
        {
            var fs = new RealFileSystem();
            var processor = new WorkflowWatchProcessor(fs, Path.Combine(existingDir, "journal"));
            using var manager = new WatchFolderManager(fs, processor);

            var rules = new[]
            {
                new WatchRule
                {
                    Name = "Valid",
                    Folder = existingDir,
                    Steps = new List<WorkflowStep> { new RenameStep(new Preset { Name = "p1" }) }
                },
                new WatchRule
                {
                    Name = "Missing",
                    Folder = missingDir,
                    Steps = new List<WorkflowStep> { new RenameStep(new Preset { Name = "p2" }) }
                }
            };

            Assert.Throws<DirectoryNotFoundException>(() => manager.Start(rules));
            Assert.Equal(0, manager.RunningCount);
        }
        finally
        {
            try { if (Directory.Exists(existingDir)) Directory.Delete(existingDir, recursive: true); } catch { }
        }
    }

    // -----------------------------------------------------------------
    // Phase 2: Extended Tokens & FileMetadata
    // -----------------------------------------------------------------

    [Fact]
    public void FileMetadata_RoundTripsWithDictionary()
    {
        var meta = new FileMetadata(
            Taken: new DateTime(2025, 4, 10, 12, 30, 0, DateTimeKind.Utc),
            Width: 4000,
            Height: 3000,
            CameraModel: "Canon EOS R5",
            AudioYear: "2024");

        var dict = meta.ToDictionary();
        var back = FileMetadata.FromDictionary(dict);

        Assert.Equal(4000, back.Width);
        Assert.Equal(3000, back.Height);
        Assert.Equal("Canon EOS R5", back.CameraModel);
        Assert.Equal("2024", back.AudioYear);
    }

    [Fact]
    public void TokenEngine_ExpandsExifAudioAndParentLevelTokens()
    {
        var file = new FileItem(
            FullPath: @"C:\Archive\2025\Trips\Japan\photo.jpg",
            Directory: @"C:\Archive\2025\Trips\Japan",
            Name: "photo",
            Extension: "jpg",
            IsDirectory: false,
            Size: 1024,
            CreatedUtc: DateTime.UtcNow,
            ModifiedUtc: DateTime.UtcNow,
            LoadOrder: 0);

        var meta = new FileMetadata(Width: 1920, Height: 1080, CameraModel: "Sony A7 IV", AudioYear: "2025").ToDictionary();
        var ctx = new RuleContext(DateTime.UtcNow, "seed", Metadata: meta);
        var state = new RenameState("photo", "jpg", 0, 0, new List<string>());
        var engine = new TokenEngine();
        var errors = new List<string>();

        var expanded = engine.Expand("{parent:3}_{parent:2}_{parent:1}_{camera}_{width}x{height}_{year}", file, state, ctx, errors);

        Assert.Empty(errors);
        Assert.Equal("2025_Trips_Japan_Sony A7 IV_1920x1080_2025", expanded);

        // Out-of-range parent level reports invalid token.
        var badErrors = new List<string>();
        var bad = engine.Expand("{parent:5}", file, state, ctx, badErrors);
        Assert.Equal("{parent:5}", bad);
        Assert.Single(badErrors);
    }

    [Fact]
    public void TokenEngine_ComputesLazyHashTokens_AndRespectsOfflineFiles()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "bfr-hash-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var payload = Encoding.UTF8.GetBytes("batch-file-renamer-v22");
            File.WriteAllBytes(tempFile, payload);
            var info = new FileInfo(tempFile);

            var expectedMd5 = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
            var expectedSha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

            var file = new FileItem(
                FullPath: tempFile,
                Directory: Path.GetDirectoryName(tempFile)!,
                Name: Path.GetFileNameWithoutExtension(tempFile),
                Extension: "txt",
                IsDirectory: false,
                Size: info.Length,
                CreatedUtc: info.CreationTimeUtc,
                ModifiedUtc: info.LastWriteTimeUtc,
                LoadOrder: 0);

            var ctx = new RuleContext(DateTime.UtcNow, "seed");
            var state = new RenameState(file.Name, file.Extension, 0, 0, new List<string>());
            var engine = new TokenEngine();
            var errors = new List<string>();

            var result = engine.Expand("{md5:8}_{sha256:8}", file, state, ctx, errors);
            Assert.Empty(errors);
            Assert.Equal($"{expectedMd5[..8]}_{expectedSha[..8]}", result);

            // Offline placeholder must not be read.
            var offlineErrors = new List<string>();
            var offlineResult = engine.Expand("{md5:8}", file with { IsOffline = true }, state, ctx, offlineErrors);
            Assert.Equal("{md5:8}", offlineResult);
            Assert.Single(offlineErrors);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void MetadataRequirement_DetectsNewExifAndYearTokens_WithoutTriggeringOnHashTokens()
    {
        Assert.True(MetadataRequirement.HasMetadataToken("{width}x{height}"));
        Assert.True(MetadataRequirement.HasMetadataToken("{camera}"));
        Assert.True(MetadataRequirement.HasMetadataToken("{year}"));
        Assert.False(MetadataRequirement.HasMetadataToken("{md5:8}_{sha256:8}_{parent:2}"));
    }
}
