using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using LingoIsland;
using LingoIsland.Present;
using Xunit;

namespace LingoIsland.Tests;

/// <summary>
/// #311 使用手冊入口：網址組字（RepoUrl＋本機版號／退路）、開啟失敗之處置、版號與關於頁同源，
/// 以及兩入口（關於頁鈕、系統匣項）之接線結構斷言（以純文字讀 XAML／.cs，不需 STA 載入）。
/// </summary>
public class UserManualTests
{
    private const string CurrentRepo = "https://github.com/twMoonBear-Laboratory/solLingoIsland";

    // ---------- 單元：網址組字 ----------

    [Fact]
    public void BuildUrl_WithVersion_PinsTagFromRepoUrl()
    {
        var url = UserManual.BuildUrl(new Version(4, 17, 0, 0));
        Assert.Equal($"{UpdateService.RepoUrl}/blob/v4.17.0/README.md", url);
        Assert.Equal(CurrentRepo + "/blob/v4.17.0/README.md", url); // 完成定義第 3 條之字面網址
    }

    [Fact]
    public void BuildUrl_IgnoresRevisionPart()
    {
        Assert.Equal($"{UpdateService.RepoUrl}/blob/v1.2.3/README.md", UserManual.BuildUrl(new Version(1, 2, 3, 9)));
    }

    [Fact]
    public void BuildUrl_NullVersion_FallsBackToMain()
    {
        Assert.Equal($"{UpdateService.RepoUrl}/blob/main/README.md", UserManual.BuildUrl(null));
    }

    [Fact]
    public void BuildUrl_ZeroVersion_FallsBackToMain()
    {
        Assert.Equal($"{UpdateService.RepoUrl}/blob/main/README.md", UserManual.BuildUrl(new Version(0, 0, 0, 0)));
        Assert.Equal($"{UpdateService.RepoUrl}/blob/main/README.md", UserManual.BuildUrl(new Version(0, 0))); // Build 未設定＝-1
    }

    [Fact]
    public void BuildUrl_TwoPartVersion_TreatsBuildAsZero()
    {
        Assert.Equal($"{UpdateService.RepoUrl}/blob/v4.17.0/README.md", UserManual.BuildUrl(new Version(4, 17)));
    }

    [Fact]
    public void BuildUrl_NoQueryStringOrUserData()
    {
        var url = UserManual.BuildUrl(new Version(4, 17, 0, 0));
        Assert.DoesNotContain("?", url);
        Assert.DoesNotContain("#", url);
        Assert.StartsWith(UpdateService.RepoUrl + "/blob/", url);
    }

    // ---------- 單元：版號與關於頁同源 ----------

    [Fact]
    public void AppVersion_Display_FormatsThreeParts()
    {
        Assert.Equal("4.17.0", AppVersion.Display(new Version(4, 17, 0, 5)));
        Assert.Equal("?", AppVersion.Display(null));
        Assert.Equal("?", AppVersion.Display(new Version(0, 0, 0, 0))); // 0.0.0＝未設定，與手冊退路同一判準
        Assert.Equal("4.17.0", AppVersion.Display(new Version(4, 17)));
    }

    /// <summary>組件版號＝根 VERSION（Directory.Build.props 投影），故手冊網址所釘之版號即使用者看到之版號。</summary>
    [Fact]
    public void AppVersion_Current_EqualsRepoVersionFile()
    {
        var root = FindRepoRoot();
        var expected = File.ReadAllText(Path.Combine(root, "VERSION")).Trim();
        Assert.Equal(expected, AppVersion.Display(AppVersion.Current));
        Assert.Equal($"{UpdateService.RepoUrl}/blob/v{expected}/README.md", UserManual.BuildUrl(AppVersion.Current));
    }

    // ---------- 單元：開啟與開啟失敗之處置 ----------

    [Fact]
    public void TryOpen_Success_PassesUrlAndReturnsNull()
    {
        string? got = null;
        Assert.Null(UserManual.TryOpen("https://example.test/x", u => got = u));
        Assert.Equal("https://example.test/x", got);
    }

    [Fact]
    public void TryOpen_LauncherThrows_ReturnsMessageWithUrl_DoesNotThrow()
    {
        var url = UserManual.BuildUrl(new Version(4, 17, 0, 0));
        var msg = UserManual.TryOpen(url, _ => throw new Win32Exception(1155, "沒有與此作業關聯的應用程式"));
        Assert.NotNull(msg);
        Assert.Contains(url, msg);
        Assert.Equal(AppStatusText.ManualOpenFailed(url), msg);
    }

    [Fact]
    public void TryOpen_AnyExceptionType_IsHandled()
    {
        Assert.NotNull(UserManual.TryOpen("u", _ => throw new InvalidOperationException()));
        Assert.NotNull(UserManual.TryOpen("u", _ => throw new FileNotFoundException()));
    }

    [Fact]
    public void Open_LauncherThrows_NotifiesOnceWithUrl()
    {
        var notified = new List<string>();
        var ok = UserManual.Open(notified.Add, _ => throw new Win32Exception(), new Version(4, 17, 0, 0), () => true);
        Assert.False(ok);
        var single = Assert.Single(notified);
        Assert.Contains($"{UpdateService.RepoUrl}/blob/v4.17.0/README.md", single);
    }

    [Fact]
    public void Open_Success_DoesNotNotify_AndOpensVersionedUrl()
    {
        var notified = new List<string>();
        string? opened = null;
        Assert.True(UserManual.Open(notified.Add, u => opened = u, new Version(4, 17, 0, 0), () => true));
        Assert.Empty(notified);
        Assert.Equal($"{UpdateService.RepoUrl}/blob/v4.17.0/README.md", opened);
    }

    /// <summary>完成定義第 4 條「離線…告知」：判定離線仍照常開啟，並經提示器告知恰一次（含網址）。</summary>
    [Fact]
    public void Open_Offline_StillOpens_AndNotifiesOnceWithUrl()
    {
        var notified = new List<string>();
        string? opened = null;
        Assert.True(UserManual.Open(notified.Add, u => opened = u, new Version(4, 17, 0, 0), () => false));
        var url = $"{UpdateService.RepoUrl}/blob/v4.17.0/README.md";
        Assert.Equal(url, opened);
        var single = Assert.Single(notified);
        Assert.Equal(AppStatusText.ManualOffline(url), single);
        Assert.Contains("沒有網路連線", single);
        Assert.Contains(url, single);
    }

    [Fact]
    public void Open_LaunchFailsWhileOffline_NotifiesOnlyOnce_WithFailureMessage()
    {
        var notified = new List<string>();
        Assert.False(UserManual.Open(notified.Add, _ => throw new Win32Exception(), new Version(4, 17, 0, 0), () => false));
        Assert.Contains("無法開啟瀏覽器", Assert.Single(notified));
    }

    [Fact]
    public void Open_NetworkProbeThrows_TreatedAsOnline_NoExtraNotice()
    {
        var notified = new List<string>();
        Assert.True(UserManual.Open(notified.Add, _ => { }, new Version(4, 17, 0, 0), () => throw new NetworkInformationException()));
        Assert.Empty(notified);
    }

    [Fact]
    public void ManualOpenFailed_MessageTellsWhatAndWhere()
    {
        var msg = AppStatusText.ManualOpenFailed("https://example.test/m");
        Assert.Contains("無法開啟瀏覽器", msg);
        Assert.Contains("https://example.test/m", msg);
    }

    /// <summary>測試縫：設定時不開瀏覽器、改附寫網址（e2e 攔截用）；寫檔失敗亦歸開啟失敗、不擲出。</summary>
    [Fact]
    public void DefaultLauncher_WithLaunchLogEnv_AppendsUrlInsteadOfOpening()
    {
        var log = Path.Combine(Path.GetTempPath(), "LingoIsland-manual-test-" + Guid.NewGuid().ToString("N") + ".log");
        var prev = Environment.GetEnvironmentVariable(UserManual.LaunchLogEnv);
        try
        {
            Environment.SetEnvironmentVariable(UserManual.LaunchLogEnv, log);
            Assert.Null(UserManual.TryOpen("https://example.test/a"));
            Assert.Null(UserManual.TryOpen("https://example.test/b"));
            Assert.Equal(new[] { "https://example.test/a", "https://example.test/b" }, File.ReadAllLines(log));

            Environment.SetEnvironmentVariable(UserManual.LaunchLogEnv, Path.Combine(log, "no-such-dir", "x.log"));
            Assert.NotNull(UserManual.TryOpen("https://example.test/c")); // 寫不進去＝開啟失敗、回訊息
        }
        finally
        {
            Environment.SetEnvironmentVariable(UserManual.LaunchLogEnv, prev);
            if (File.Exists(log)) File.Delete(log);
        }
    }

    // ---------- 整合（結構）：兩入口接線 ----------

    [Fact]
    public void AboutPageXaml_HasManualButton_GhostStyle_RightOfChangeLogInSameRow()
    {
        var xaml = ReadRepoFile("sysLingoIsland", "modPresent", "AboutPage.xaml");
        var btn = Regex.Match(xaml, "<Button\\s+x:Name=\"ManualBtn\"[^>]*>");
        Assert.True(btn.Success, "AboutPage.xaml 找不到 ManualBtn");
        Assert.Contains("Content=\"使用手冊\"", btn.Value);
        Assert.Contains("Style=\"{StaticResource GhostButton}\"", btn.Value);
        Assert.DoesNotContain("Visibility=", btn.Value); // 恆顯，不隨更新區隱藏

        var log = xaml.IndexOf("x:Name=\"ChangeLogBtn\"", StringComparison.Ordinal);
        Assert.True(log > 0, "AboutPage.xaml 找不到 ChangeLogBtn");
        Assert.True(btn.Index > log, "ManualBtn 應位於 ChangeLogBtn 之後（右側）");
        var between = xaml.Substring(log, btn.Index - log);
        Assert.DoesNotContain("</StackPanel>", between); // 同一橫排容器＝同列
        Assert.DoesNotContain("x:Name=\"UpdatePanel\"", between); // 不在可隱之更新區內
    }

    [Fact]
    public void AboutPageCode_WiresManualButtonToUserManual_AndUsesSharedVersion()
    {
        var cs = ReadRepoFile("sysLingoIsland", "modPresent", "AboutPage.xaml.cs");
        Assert.Matches(new Regex("ManualBtn\\.Click\\s*\\+=[^;]*UserManual\\.Open\\(", RegexOptions.Singleline), cs);
        Assert.Contains("AppVersion.Display(AppVersion.Current)", cs); // 版號顯示與手冊網址同源
        Assert.DoesNotContain("GetExecutingAssembly", cs);
    }

    [Fact]
    public void TrayMenu_HasManualItem_AfterAbout_BeforeExit_WiredToUserManual()
    {
        var cs = ReadRepoFile("sysLingoIsland", "App.xaml.cs");
        var item = Regex.Match(cs, "menu\\.Items\\.Add\\(\"使用手冊\",[^\\n]*UserManual\\.Open\\(");
        Assert.True(item.Success, "App.xaml.cs 系統匣選單找不到「使用手冊」項接 UserManual.Open");
        var about = cs.IndexOf("menu.Items.Add(\"關於\"", StringComparison.Ordinal);
        var exit = cs.IndexOf("menu.Items.Add(\"結束\"", StringComparison.Ordinal);
        Assert.True(about > 0 && exit > 0, "找不到「關於」或「結束」項");
        Assert.True(about < item.Index && item.Index < exit, "「使用手冊」應位於「關於」之後、「結束」之前");
    }

    // ---------- helpers ----------

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VERSION")) && Directory.Exists(Path.Combine(dir.FullName, "sysLingoIsland")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("找不到 repo 根（含 VERSION 與 sysLingoIsland）");
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = FindRepoRoot();
        Array.Copy(parts, 0, all, 1, parts.Length);
        return File.ReadAllText(Path.Combine(all));
    }
}
