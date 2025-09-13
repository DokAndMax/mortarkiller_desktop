using mortarkiller;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace WinFormsApp1;

// Єдиний "вхід" у застосунок, що вміє всі режими
public static class ProgramUnified
{
    public static async Task<int> Run(string[] args)
    {
        if (args == null || args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        string root = args[0].ToLowerInvariant();
        string[] rest = args.Skip(1).ToArray();

        switch (root)
        {
            // Об'єднаний live режим: Grid + Pins + Player Marks
            case "combined-live":
                return await ProgramCombined.MainCombined(rest);

            // Режими grid (train/detect/live)
            case "grid":
                return ProgramGrid.Run(rest);

            // Аліаси: grid-train / grid-detect / grid-live
            case "grid-train":
            case "grid-detect":
            case "grid-live":
                return ProgramGrid.Run([.. SplitAlias(root), .. rest]);

            // Режими pin (train/detect/live)
            case "pin":
                return await ProgramPin.Run(rest);

            // Аліаси: pin-train / pin-detect / pin-live
            case "pin-train":
            case "pin-detect":
            case "pin-live":
                return await ProgramPin.Run([.. SplitAlias(root), .. rest]);

            // Режим players (train)
            case "players":
            case "player":
                return ProgramMark.Run(rest);

            // Аліаси: players-train / player-train
            case "players-train":
            case "player-train":
                return ProgramMark.Run([.. SplitAlias(root), .. rest]);

            case "-h":
            case "--help":
            case "help":
                PrintUsage();
                return 0;

            default:
                Console.WriteLine($"Unknown command: {args[0]}");
                PrintUsage();
                return 1;
        }
    }

    private static string[] SplitAlias(string alias)
    {
        int i = alias.IndexOf('-');
        if (i <= 0) return new[] { alias };
        return new[] { alias[..i], alias[(i + 1)..] }; // ["grid","train"] тощо
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  app combined-live <grid_params.json> <pin_best_params.json> <players_params.json> \"Process Name\" [--interval=500]");
        Console.WriteLine();
        Console.WriteLine("  app grid train dataset.json outDir");
        Console.WriteLine("  app grid detect image.png params.json outDir");
        Console.WriteLine("  app grid live params.json [processName=TslGame] [intervalMs=500] [showOverlay=0] [maxDim=1280]");
        Console.WriteLine();
        Console.WriteLine("  app pin train config.json");
        Console.WriteLine("  app pin detect output/best_params.json <image_or_dir> [more]");
        Console.WriteLine("  app pin live output/best_params.json \"Process Name\"");
        Console.WriteLine();
        Console.WriteLine("  app players train config.json");
        Console.WriteLine();
        Console.WriteLine("Aliases:");
        Console.WriteLine("  grid-train, grid-detect, grid-live, pin-train, pin-detect, pin-live, players-train");
    }
}