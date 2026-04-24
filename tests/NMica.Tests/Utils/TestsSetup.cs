using System;

namespace NMica.Tests.Utils
{
    /// <summary>
    /// Shared across all tests in a docker-collection. Defines the SDK image used to build/run test
    /// solutions inside containers.
    /// </summary>
    public class TestsSetup : IDisposable
    {
        public const string NMicaVersion = "1.0.0-test";
        public string SdkImage { get; } = Environment.GetEnvironmentVariable("NMICA_TEST_SDK_IMAGE") ?? "mcr.microsoft.com/dotnet/sdk:10.0";

        public static string SdkImageFor(string targetFramework) =>
            $"mcr.microsoft.com/dotnet/sdk:{targetFramework.Replace("net", string.Empty)}";

        public void Dispose() { }
    }
}
