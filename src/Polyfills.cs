// Without this attribute .NET runs the app in legacy compatibility mode (old TLS defaults, old WinForms behavior).
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

// Lets modern C# records compile against .NET Framework 4.8.
namespace System.Runtime.CompilerServices
{
    static class IsExternalInit { }
}
