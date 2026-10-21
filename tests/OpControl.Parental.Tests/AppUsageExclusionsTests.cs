using OpControl.Parental.Server.Services;
using Xunit;

namespace OpControl.Parental.Tests;

public class AppUsageExclusionsTests
{
    [Theory]
    [InlineData("win32:shellexperiencehost.exe|microsoftcorporation", "Windows Shell Experience Host")]
    [InlineData("win32:startmenuexperiencehost.exe|microsoftcorporation", "Start")]
    [InlineData("win32:searchhost.exe|microsoftcorporation", "Search")]
    [InlineData("win32:searchapp.exe|microsoftcorporation", "Search")]
    [InlineData("win32:textinputhost.exe|microsoftcorporation", "Windows Input Experience")]
    [InlineData("win32:applicationframehost.exe|microsoftcorporation", "Application Frame Host")]
    [InlineData("win32:lockapp.exe|microsoftcorporation", "Lock App")]
    [InlineData("win32:runtimebroker.exe|microsoftcorporation", "Runtime Broker")]
    [InlineData("win32:dllhost.exe|microsoftcorporation", "COM Surrogate")]
    [InlineData("win32:svchost.exe|microsoftcorporation", "Service Host: Local System")]
    [InlineData("win32:dwm.exe|microsoftcorporation", "Desktop Window Manager")]
    [InlineData("win32:systemsettings.exe|microsoftcorporation", "Settings")]
    [InlineData("store:microsoft.windows.startmenuexperiencehost_cw5n1h2txyewy", "Start")]
    [InlineData("store:microsoft.lockapp_cw5n1h2txyewy", "Lock App")]
    [InlineData("store:microsoft.windows.search_cw5n1h2txyewy", "Search")]
    public void Shell_hosts_and_system_ui_are_excluded(string identity, string display) =>
        Assert.True(AppUsageExclusions.IsExcluded(identity, display));

    [Theory]
    [InlineData("win32:opcontrol.parental.helper.exe|opcontrolitsolutions", "OpControl Parental Helper")]
    [InlineData("win32:opcontrol.parental.client.exe|opcontrolitsolutions", "OpControl Parental Client")]
    [InlineData("win32:myapp.exe|somepublisher", "OpControl")]
    [InlineData("path:c:\\program files\\opcontrolparental\\helper\\opcontrol.parental.helper.exe", "")]
    public void Our_own_processes_are_excluded(string identity, string display) =>
        Assert.True(AppUsageExclusions.IsExcluded(identity, display));

    [Theory]
    [InlineData("win32:explorer.exe|microsoftcorporation", "Windows Explorer")]
    [InlineData("win32:explorer.exe|microsoftcorporation", "")]
    public void Windows_explorer_shell_is_excluded(string identity, string display) =>
        Assert.True(AppUsageExclusions.IsExcluded(identity, display));

    [Theory]
    [InlineData("win32:notepad.exe|microsoftcorporation", "Notepad")]
    [InlineData("win32:chrome.exe|google", "Google Chrome")]
    [InlineData("win32:explorer.exe|microsoftcorporation", "File Explorer")]
    [InlineData("win32:explorer.exe|microsoftcorporation", "Explorador de archivos")]
    [InlineData("store:microsoft.windowsterminal_8wekyb3d8bbwe", "Windows Terminal")]
    [InlineData("store:microsoft.windowsnotepad_8wekyb3d8bbwe", "Notepad")]
    [InlineData("path:c:\\games\\game.exe", "My Game")]
    public void Real_apps_and_file_explorer_are_kept(string identity, string display) =>
        Assert.False(AppUsageExclusions.IsExcluded(identity, display));
}
