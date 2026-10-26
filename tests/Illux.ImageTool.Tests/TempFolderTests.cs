using System.Security.AccessControl;
using System.Security.Principal;
using Illux.ImageTool.Batch;
using Illux.ImageTool.Options;
using ImageMagick;

namespace Illux.ImageTool.Tests;

/// <summary>The temporary folder FileProcessor writes into (R46).</summary>
public sealed class TempFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-tempfolder-" + Guid.NewGuid().ToString("N"));

    public TempFolderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Out => Path.Combine(_dir, "Out");

    [Fact]
    public void Create_ProtectedAcl_OnlySystemAdministratorsAndThisAccountHaveAccessToTheFolder()
    {
        var temp = Path.Combine(Out, FileProcessor.TempPrefix + Guid.NewGuid().ToString("N"));

        TempFolder.Create(temp);

        var security = new DirectoryInfo(temp).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        var onFolder = rules.Where(r => !r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)).ToList();
        using var me = WindowsIdentity.GetCurrent();
        string[] expected =
        [
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            me.User!.Value,
        ];
        Assert.Equal(expected.Order(), onFolder.Select(r => r.IdentityReference.Value).Order());
        Assert.All(onFolder, r =>
        {
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights);
            Assert.False(r.IsInherited);
        });
        // Every other entry only reaches files created inside (so they get the output folder's permissions).
        Assert.All(rules.Except(onFolder), r => Assert.Equal(InheritanceFlags.ObjectInherit, r.InheritanceFlags));
        Assert.Null(TempFolder.WhyNotOurs(new DirectoryInfo(temp)));
    }

    [Fact]
    public void WhyNotOurs_OrdinaryFolder_IsNotOurs()
    {
        var planted = Directory.CreateDirectory(Path.Combine(_dir, FileProcessor.TempPrefix + "planted"));

        Assert.NotNull(TempFolder.WhyNotOurs(planted));
    }

    [Fact]
    public async Task Output_HasTheSamePermissionsAsAFileCreatedInOut()
    {
        var input = TestImages.Write(Path.Combine(_dir, "In"), "a.jpg", 60, 40, ColorSpace.sRGB, 8, 200);
        Directory.CreateDirectory(Out);
        var reference = Path.Combine(Out, "reference.txt");
        File.WriteAllText(reference, "x");

        var result = await FileProcessor.ProcessAsync(input, Out, ProcessingOptions.Default, NamePolicy.Overwrite,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal(Rules(reference), Rules(Path.Combine(Out, "a.jpg")));
    }

    private static string[] Rules(string file) =>
        new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => $"{r.IdentityReference.Value} {r.AccessControlType} {(int)r.FileSystemRights:X}")
            .Order()
            .ToArray();
}
