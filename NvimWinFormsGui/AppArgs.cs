using System;

namespace NvimWinFormsGui
{
    public sealed class AppArgs
    {
        public string? FilePath { get; init; }
        public int? Line { get; init; }

        public static AppArgs Parse(string[] args)
        {
            string? file = null;
            int? line = null;

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];

                if (a == "-l" || a == "--line")
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("'-l' の後に行番号が必要です。");

                    if (!int.TryParse(args[i + 1], out var n) || n <= 0)
                        throw new ArgumentException("行番号は 1 以上の整数で指定してください。");

                    line = n;
                    i++;
                    continue;
                }

                // オプションではない最初の引数をファイルパスとして扱う
                if (!a.StartsWith("-") && file is null)
                {
                    file = a;
                    continue;
                }
            }

            return new AppArgs { FilePath = file, Line = line };
        }
    }
}
