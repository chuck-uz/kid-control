using System.Xml.Linq;
using FluentAssertions;
using KidControl.Contracts;
using KidControl.Infrastructure.Update;
using Xunit;

namespace KidControl.Infrastructure.Tests;

/// <summary>
/// Covers the one bug-prone, platform-agnostic part of the detached updater: the scheduled-task
/// XML. The rest (schtasks invocation, the swap itself) is Windows-only and exercised in the
/// field, but the XML generation — SID, run level, command/args, escaping — is pure and testable
/// here so a malformed task can't ship unnoticed.
/// </summary>
public sealed class ScheduledTaskUpdateLauncherTests
{
    private const string TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void Task_xml_is_wellformed_system_ondemand_apply_update()
    {
        var xml = ScheduledTaskUpdateLauncher.BuildTaskXml(
            @"C:\ProgramData\KidControl\updates\v2.2.0\extracted\KidControl.Installer.exe",
            @"C:\ProgramData\KidControl\updates\v2.2.0\extracted");

        var doc = XDocument.Parse(xml); // throws if malformed
        XName N(string local) => XName.Get(local, TaskNs);

        // Runs as LocalSystem at the highest run level.
        doc.Descendants(N("UserId")).Single().Value.Should().Be("S-1-5-18");
        doc.Descendants(N("RunLevel")).Single().Value.Should().Be("HighestAvailable");

        // On-demand: no trigger at all (the service triggers it via schtasks /Run).
        doc.Descendants(N("Triggers")).Should().BeEmpty();

        // Bounded so a hung updater is reaped, and does not fight a hard terminate.
        doc.Descendants(N("ExecutionTimeLimit")).Single().Value.Should().Be("PT10M");

        // The action is the installer in /apply-update mode against the staged source.
        doc.Descendants(N("Command")).Single().Value.Should().EndWith("KidControl.Installer.exe");
        var args = doc.Descendants(N("Arguments")).Single().Value;
        args.Should().Contain("/apply-update");
        args.Should().Contain(@"--source ""C:\ProgramData\KidControl\updates\v2.2.0\extracted""");
    }

    [Fact]
    public void Task_name_matches_the_shared_contract()
    {
        // The service registers it and the updater/uninstaller reference the same name.
        KidControlNames.UpdateTaskName.Should().Be("KidControl.Update.Apply");
    }

    [Fact]
    public void Xml_special_characters_in_paths_are_escaped()
    {
        // A path with an ampersand must not break the XML.
        var xml = ScheduledTaskUpdateLauncher.BuildTaskXml(
            @"C:\R&D\KidControl.Installer.exe", @"C:\R&D\src");

        var doc = XDocument.Parse(xml); // would throw on a raw '&'
        XName N(string local) => XName.Get(local, TaskNs);
        doc.Descendants(N("Command")).Single().Value.Should().Be(@"C:\R&D\KidControl.Installer.exe");
        doc.Descendants(N("Arguments")).Single().Value.Should().Contain(@"C:\R&D\src");
    }
}
