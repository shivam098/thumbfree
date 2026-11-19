using System.Runtime.CompilerServices;
using ThumbFree.App.Platform;

namespace ThumbFree.App.Tests;

/// <summary>Runs once, before any test.</summary>
internal static class TestSetup
{
    /// <summary>A browser that only the (test) registry knows about.</summary>
    public const string RegisteredBrowser = "ExampleBrowser";

    /// <summary>Tests never read this PC's registry: the browsers registered there are just <see cref="RegisteredBrowser"/>.</summary>
    [ModuleInitializer]
    internal static void Start() => Browsers.ReadRegisteredFrom(() => [RegisteredBrowser]);
}