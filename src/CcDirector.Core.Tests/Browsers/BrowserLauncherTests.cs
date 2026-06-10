using CcDirector.Core.Browsers;
using Xunit;

namespace CcDirector.Core.Tests.Browsers;

public class BrowserLauncherTests
{
    // A trimmed Local State document mirroring the real info_cache shape (folder -> name/user_name/gaia_name).
    private const string SampleLocalState = """
    {
      "profile": {
        "info_cache": {
          "Default": { "name": "Person 1", "user_name": "", "gaia_name": "" },
          "Profile 2": { "name": "centerconsulting.com", "user_name": "soren@centerconsulting.com", "gaia_name": "Soren C" },
          "Profile 1": { "name": "duksrevo.com", "user_name": "soren@duksrevo.com", "gaia_name": "Soren D" }
        }
      }
    }
    """;

    [Fact]
    public void ParseProfiles_ReadsFolderNameDisplayNameAndAccount()
    {
        var profiles = BrowserLauncher.ParseProfiles(SampleLocalState);

        var center = Assert.Single(profiles, p => p.FolderName == "Profile 2");
        Assert.Equal("centerconsulting.com", center.DisplayName);
        Assert.Equal("soren@centerconsulting.com", center.Account);
    }

    [Fact]
    public void ParseProfiles_NoAccount_UsesNullAccount()
    {
        var profiles = BrowserLauncher.ParseProfiles(SampleLocalState);

        var person = Assert.Single(profiles, p => p.FolderName == "Default");
        Assert.Equal("Person 1", person.DisplayName);
        Assert.Null(person.Account);
    }

    [Fact]
    public void ParseProfiles_FallsBackToGaiaNameWhenUserNameEmpty()
    {
        const string json = """
        { "profile": { "info_cache": {
            "Profile 1": { "name": "Cody", "user_name": "", "gaia_name": "Cody Frederiksen" }
        } } }
        """;

        var profile = Assert.Single(BrowserLauncher.ParseProfiles(json));
        Assert.Equal("Cody Frederiksen", profile.Account);
    }

    [Fact]
    public void ParseProfiles_SortsAccountBearingProfilesFirstThenByName()
    {
        var profiles = BrowserLauncher.ParseProfiles(SampleLocalState);

        // Account-bearing profiles (centerconsulting.com, duksrevo.com) come before the
        // accountless "Person 1", and within the account group they sort by display name.
        Assert.Equal(new[] { "Profile 2", "Profile 1", "Default" }, profiles.Select(p => p.FolderName).ToArray());
    }

    [Fact]
    public void ParseProfiles_MissingInfoCache_ReturnsEmpty()
    {
        Assert.Empty(BrowserLauncher.ParseProfiles("{ \"profile\": {} }"));
    }

    [Fact]
    public void ParseProfiles_EmptyJson_Throws()
    {
        Assert.Throws<ArgumentException>(() => BrowserLauncher.ParseProfiles(""));
    }

    [Fact]
    public void GetProfiles_ReadsFromBrowserLocalStateFile()
    {
        var userDataDir = Path.Combine(Path.GetTempPath(), "cc-director-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDataDir);
        try
        {
            File.WriteAllText(Path.Combine(userDataDir, "Local State"), SampleLocalState);
            var browser = new BrowserInfo(BrowserKind.Edge, "Microsoft Edge", "C:\\nonexistent\\msedge.exe", userDataDir);

            var profiles = BrowserLauncher.GetProfiles(browser);

            Assert.Equal(3, profiles.Count);
            Assert.Contains(profiles, p => p.FolderName == "Profile 1");
        }
        finally
        {
            Directory.Delete(userDataDir, recursive: true);
        }
    }

    [Fact]
    public void GetProfiles_NoLocalStateFile_ReturnsEmpty()
    {
        var browser = new BrowserInfo(BrowserKind.Chrome, "Google Chrome", "C:\\nonexistent\\chrome.exe",
            Path.Combine(Path.GetTempPath(), "cc-director-missing-" + Guid.NewGuid().ToString("N")));

        Assert.Empty(BrowserLauncher.GetProfiles(browser));
    }

    [Fact]
    public void OpenWithProfile_MissingExe_ThrowsNamingTheExe()
    {
        var browser = new BrowserInfo(BrowserKind.Edge, "Microsoft Edge", "C:\\nope\\msedge.exe", "C:\\nope\\User Data");

        var ex = Assert.Throws<FileNotFoundException>(
            () => BrowserLauncher.OpenWithProfile("https://example.com", browser, "Profile 1"));
        Assert.Contains("msedge.exe", ex.Message);
    }

    [Fact]
    public void OpenWithProfile_MissingProfileFolder_ThrowsNamingTheProfile()
    {
        // Use a real existing exe (cmd.exe) so the exe check passes and the profile-folder check fails.
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var fakeExe = Path.Combine(system32, "cmd.exe");
        var userDataDir = Path.Combine(Path.GetTempPath(), "cc-director-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDataDir);
        try
        {
            var browser = new BrowserInfo(BrowserKind.Edge, "Microsoft Edge", fakeExe, userDataDir);

            var ex = Assert.Throws<DirectoryNotFoundException>(
                () => BrowserLauncher.OpenWithProfile("https://example.com", browser, "Profile 9"));
            Assert.Contains("Profile 9", ex.Message);
        }
        finally
        {
            Directory.Delete(userDataDir, recursive: true);
        }
    }
}
