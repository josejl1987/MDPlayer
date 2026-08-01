using BenchmarkDotNet.Running;
using System;

namespace BenchmarkSuite1
{
    internal class Program
    {
        static void Main(string[] args)
        {
            RendererBenchmarkReport.Write();
            if (Array.Exists(args, a => string.Equals(a, "--run-benchmarks", StringComparison.OrdinalIgnoreCase)))
                _ = BenchmarkRunner.Run(typeof(Program).Assembly);
            else
                Console.WriteLine("BenchmarkDotNet skipped (pass --run-benchmarks to run bounded microbenchmarks).");
        }
    }
}
