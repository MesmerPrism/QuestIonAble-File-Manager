using System.Security.AccessControl;
using System.Security.Principal;
using QuestIonAbleFileManager.Setup;

namespace QuestIonAbleFileManager.Setup.Tests;

public sealed class SetupStagingDirectoryTests
{
    [Fact]
    public void ProtectedStagingUsesMachineOwnedProgramFilesRoot()
    {
        Assert.Equal(
            Environment.SpecialFolder.ProgramFiles,
            SetupStagingDirectory.ProtectedRootFolder);
    }

    [Fact]
    public void ProtectedSecurityAcceptsTheIssuedDescriptor()
    {
        SetupStagingDirectory.ValidateProtectedSecurity(
            SetupStagingDirectory.CreateProtectedSecurity());
    }

    [Fact]
    public void ProtectedSecurityAcceptsEquivalentPartitionedRights()
    {
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var system = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            null);
        var security = NewProtectedSecurity(administrators);
        AddRule(security, administrators, FileSystemRights.FullControl);
        AddRule(security, system, FileSystemRights.Modify);
        AddRule(
            security,
            system,
            (FileSystemRights)((int)FileSystemRights.FullControl ^
                               (int)FileSystemRights.Modify));

        SetupStagingDirectory.ValidateProtectedSecurity(security);
    }

    [Fact]
    public void ProtectedSecurityRejectsAnAdditionalPrincipal()
    {
        var security = SetupStagingDirectory.CreateProtectedSecurity();
        AddRule(
            security,
            new SecurityIdentifier(
                WellKnownSidType.BuiltinUsersSid,
                null),
            FileSystemRights.ReadAndExecute);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SetupStagingDirectory.ValidateProtectedSecurity(security));

        Assert.Contains("unexpected_principal", exception.Message);
    }

    [Fact]
    public void ProtectedSecurityRejectsAWeakerRequiredPrincipal()
    {
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var system = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            null);
        var security = NewProtectedSecurity(administrators);
        AddRule(security, administrators, FileSystemRights.FullControl);
        AddRule(security, system, FileSystemRights.Modify);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SetupStagingDirectory.ValidateProtectedSecurity(security));

        Assert.Contains("required_full_control", exception.Message);
    }

    private static DirectorySecurity NewProtectedSecurity(
        SecurityIdentifier owner)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.SetOwner(owner);
        return security;
    }

    private static void AddRule(
        DirectorySecurity security,
        SecurityIdentifier identity,
        FileSystemRights rights)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            rights,
            InheritanceFlags.ContainerInherit |
            InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }
}
