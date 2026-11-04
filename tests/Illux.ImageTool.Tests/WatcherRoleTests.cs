using Illux.ImageTool.Runtime;
using Illux.ImageTool.Watcher;

namespace Illux.ImageTool.Tests;

/// <summary>V15: on macOS the watcher runs as two launchd jobs, each with its own role; Windows keeps one service.</summary>
public sealed class WatcherRoleTests
{
    [Fact]
    public void Windows_NoArguments_IsTheSingleServiceDoingEverything()
    {
        Assert.Equal(WatcherRole.All, WatcherRoles.Parse([], windows: true, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--role", "updates")]
    [InlineData("--role", "folders")]
    public void Windows_TakesNoRoleArgument(string argument, string value)
    {
        Assert.Null(WatcherRoles.Parse([argument, value], windows: true, out var error));
        Assert.Contains("Windows", error);
    }

    [Theory]
    [InlineData("updates", WatcherRole.Updates)]
    [InlineData("folders", WatcherRole.Folders)]
    public void Mac_TheTwoRoles(string value, WatcherRole expected)
    {
        Assert.Equal(expected, WatcherRoles.Parse(["--role", value], windows: false, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData]
    [InlineData("--role")]
    [InlineData("--role", "all")]
    [InlineData("--role", "Updates")]
    [InlineData("--role", "updates", "--role", "folders")]
    [InlineData("updates")]
    public void Mac_WithoutAValidRole_IsRefused(params string[] args)
    {
        Assert.Null(WatcherRoles.Parse(args, windows: false, out var error));
        Assert.Contains("--role updates", error);
        Assert.Contains("--role folders", error);
    }

    [Fact]
    public void Roles_WhatEachOneRuns()
    {
        Assert.True(WatcherRoles.RunsFolders(WatcherRole.All) && WatcherRoles.RunsUpdates(WatcherRole.All));
        Assert.True(WatcherRoles.RunsUpdates(WatcherRole.Updates) && !WatcherRoles.RunsFolders(WatcherRole.Updates));
        Assert.True(WatcherRoles.RunsFolders(WatcherRole.Folders) && !WatcherRoles.RunsUpdates(WatcherRole.Folders));
    }

    [Fact]
    public void FolderJobFolders_AreBelowRoot()
    {
        Assert.Equal(Path.Combine(AppPaths.Root, "folder-account"), AppPaths.FolderAccountFile);
        Assert.Equal(Path.Combine(AppPaths.Root, "folder-work"), AppPaths.FolderWorkDir);
        Assert.Equal(Path.Combine(AppPaths.Root, "folder-logs"), AppPaths.FolderLogsDir);
    }
}
