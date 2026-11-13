// A disposable game-process fixture; touches only its own test directory.
using System;
using System.IO;
using System.Threading;
static class FakeGame {
    static void Main() {
        Thread.Sleep(2000);
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixture-game-save");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "save.dat"), "progress-after-game-exit");
    }
}
