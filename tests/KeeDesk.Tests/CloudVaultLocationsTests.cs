using System.Text.Json;
using KeeDesk.Core.Services;

namespace KeeDesk.Tests;

public class CloudVaultLocationsTests
{
    [Fact]
    public void FindsCloudProviderFoldersAndDeduplicatesAliases()
    {
        string home = Path.Combine(Path.GetTempPath(), $"keedesk-cloud-{Guid.NewGuid():N}");
        try
        {
            var cloud = Path.Combine(home, "Library", "CloudStorage");
            string google = Directory.CreateDirectory(Path.Combine(cloud, "GoogleDrive-example")).FullName;
            string oneDrive = Directory.CreateDirectory(Path.Combine(cloud, "OneDrive-Team")).FullName;
            string dropbox = Directory.CreateDirectory(Path.Combine(home, "Custom Folder")).FullName;
            string iCloud = Directory.CreateDirectory(Path.Combine(home, "Library", "Mobile Documents", "com~apple~CloudDocs")).FullName;
            Directory.CreateDirectory(Path.Combine(cloud, "Unrelated"));
            Directory.CreateDirectory(Path.Combine(home, ".dropbox"));
            File.WriteAllText(Path.Combine(home, ".dropbox", "info.json"),
                JsonSerializer.Serialize(new { personal = new { path = dropbox } }));

            var roots = CloudVaultLocations.Discover(home, "", "", macOS: true, windows: false,
                oneDrive, oneDrive, Path.Combine(home, "absent"));
            Assert.Contains(new CloudVaultLocation(CloudVaultProvider.GoogleDrive, google), roots);
            Assert.Contains(new CloudVaultLocation(CloudVaultProvider.ICloudDrive, iCloud), roots);
            Assert.Contains(new CloudVaultLocation(CloudVaultProvider.Dropbox, dropbox), roots);
            Assert.Equal(oneDrive, Assert.Single(roots, location => location.Provider == CloudVaultProvider.OneDrive).DirectoryPath);
            Assert.DoesNotContain(roots, location => Path.GetFileName(location.DirectoryPath) is "Unrelated" or "absent");
        }
        finally { Directory.Delete(home, recursive: true); }
    }
}
