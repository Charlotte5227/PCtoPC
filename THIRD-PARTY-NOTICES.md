# External components

PCtoPC invokes the user's separately installed components. Their executables and source code are not bundled or copied into this repository.

- [Sunshine](https://github.com/LizardByte/Sunshine): screen/audio capture, encoding and remote input; consult its license and distribution terms.
- [Moonlight Qt](https://github.com/moonlight-stream/moonlight-qt): streaming client; consult its license and distribution terms. CLI integration is based on the public [commandlineparser.cpp](https://github.com/moonlight-stream/moonlight-qt/blob/master/app/cli/commandlineparser.cpp).
- [Tailscale](https://tailscale.com/): optional private connectivity; consult its license and service terms.
- [Microsoft .NET](https://dotnet.microsoft.com/): runtime; self-contained packages include Microsoft's runtime and its accompanying third-party notices.

PCtoPC is an independent application and is not endorsed by these projects. The PCtoPC source license does not replace the licenses of external components or runtime files included by the .NET publish tool.
