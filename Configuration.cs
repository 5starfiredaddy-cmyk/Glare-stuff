using System;
using Dalamud.Configuration;

namespace GlareCounter;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool ShowWindow { get; set; } = true;
    public long LifetimeTotal { get; set; } = 0;
}
