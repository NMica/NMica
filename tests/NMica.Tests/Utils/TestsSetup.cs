using System;

namespace NMica.Tests.Utils
{
    /// <summary>
    /// Shared across all tests in a docker-collection. Defines the SDK image used to build/run test
    /// solutions inside containers.
    /// </summary>
    public class TestsSetup
    {
        /// <summary>Version of the freshly-built NMica package, resolved from its filename.</summary>
        public static string NMicaVersion => TestPaths.NMicaVersion;

        public string SdkImage { get; } = Environment.GetEnvironmentVariable("NMICA_TEST_SDK_IMAGE") ?? "mcr.microsoft.com/dotnet/sdk:10.0";
    }
}
