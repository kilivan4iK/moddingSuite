using System;
using System.Linq;
using System.Runtime.InteropServices;
using moddingSuite.Cli;

namespace moddingSuite
{
    // Single-executable entry point. Dispatches by first argument:
    //   moddingSuite.exe atlas --warno-root … --asset-path … --out-json … --cache-dir …
    //   moddingSuite.exe gfx   …
    //   moddingSuite.exe ess   <file.ess> [more.ess …]
    //   moddingSuite.exe tgv   <file.tgv> [--atlas …]
    // Anything else (no args or unrecognised first arg) → launches the WPF GUI.
    public static class EntryPoint
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        private const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        public static int Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                string subcommand = args[0].Trim().ToLowerInvariant();
                if (IsCliSubcommand(subcommand))
                {
                    // We're a WinExe, so by default Console.WriteLine goes
                    // nowhere. Attach to the launching cmd.exe so callers
                    // (build scripts, Blender plugin, manual `> output.txt`)
                    // see the CLI output.
                    AttachConsole(ATTACH_PARENT_PROCESS);

                    string[] rest = args.Skip(1).ToArray();
                    return subcommand switch
                    {
                        "atlas" => AtlasCliRunner.Run(rest),
                        "gfx"   => GfxCliRunner.Run(rest),
                        "ess"   => EssCliRunner.Run(rest),
                        "tgv"   => TgvCliRunner.Run(rest),
                        _       => 1,
                    };
                }
            }

            // GUI mode — hand off to the App class normally.
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }

        private static bool IsCliSubcommand(string s)
        {
            return s == "atlas" || s == "gfx" || s == "ess" || s == "tgv";
        }
    }
}
