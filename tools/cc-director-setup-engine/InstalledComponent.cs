namespace CcDirector.Setup.Engine;

/// <summary>The on-disk state of a component: present or not, and at what version.</summary>
/// <param name="ComponentId">The component this describes.</param>
/// <param name="Present">True if the component's file exists on disk.</param>
/// <param name="Version">The installed version, or null if absent / unreadable.</param>
/// <param name="Path">The resolved on-disk path that was inspected.</param>
public sealed record InstalledComponent(string ComponentId, bool Present, string? Version, string Path);
