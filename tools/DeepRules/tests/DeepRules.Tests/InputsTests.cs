using Xunit;
using static DeepRules.Tests.Harness;

namespace DeepRules.Tests
{
    public class DR021Tests
    {
        [Fact]
        public void PathCombineWithNameParameter() => Fires("DR021", """
using System.IO;
class Exporter
{
    private readonly string _root = "ExportedGrids";
    public string Target(string name) => Path.Combine(_root, name + ".sbc");
}
""", 1, "Path.Combine");

        [Fact]
        public void ComposedPathIntoFileAndFileInfo() => Fires("DR021", """
using System.IO;
class Importer
{
    private readonly string _dir = "Imports";
    public string Read(string file) => File.ReadAllText(_dir + "/" + file);
    public long Size(string name) { var fi = new FileInfo($"{_dir}/{name}.xml"); return fi.Length; }
}
""", 2);

        [Fact]
        public void GetFileNameOrRootCheckIsSilent() => Silent("DR021", """
using System;
using System.IO;
class Exporter
{
    private readonly string _root = "ExportedGrids";
    public string Target(string name) => Path.Combine(_root, Path.GetFileName(name) + ".sbc");
    public string Checked(string name)
    {
        var full = Path.GetFullPath(Path.Combine(_root, name));
        if (!full.StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(name);
        return full;
    }
}
""");

        [Fact]
        public void BarePathParameterAndLinqElementAreSilent() => Silent("DR021", """
using System.IO;
using System.Linq;
class Store
{
    public string Load(string path) => File.ReadAllText(path);
    public string[] Names(string[] files) => files.Select(f => Path.GetExtension(f)).ToArray();
    public string[] Under(string[] files) => files.Select(f => Path.Combine("data", f)).ToArray();
}
""");
    }

    public class DR022Tests
    {
        [Fact]
        public void BalancePlusParameter()
        {
            var d = Fires("DR022", """
class Account
{
    public long Balance { get; private set; }
    public void Deposit(long amount) { Balance += amount; }
}
""", 1, "Balance += amount");
            Assert.Contains("negative", d[0].GetMessage());
        }

        [Fact]
        public void ScoreAndCreditsUnbounded() => Fires("DR022", """
class Board
{
    private int _totalScore;
    private decimal _credits;
    public void Add(int points) { _totalScore = _totalScore + points; }
    public void Charge(decimal cost) { _credits -= cost; }
}
""", 2);

        [Fact]
        public void ComparedButNotAgainstZeroIsNegativeLead()
        {
            var d = Fires("DR022", """
class Account
{
    private long _balance;
    public bool Pay(long amount)
    {
        if (amount > _balance) return false;
        _balance -= amount;
        return true;
    }
}
""", 1);
            Assert.Contains("never checks against zero", d[0].GetMessage());
        }

        [Fact]
        public void ValidatedAmountIsSilent() => Silent("DR022", """
class Account
{
    private const long Max = 1000000;
    public long Balance { get; private set; }
    public void Deposit(long amount) { if (amount <= 0 || amount > Max) return; Balance += amount; }
    public void Bonus(long amount) { Balance += System.Math.Min(amount, Max); }
}
""");

        [Fact]
        public void CheckedAndConstantStepsAreSilent() => Silent("DR022", """
class Counter
{
    private int _count;
    private long _total;
    public void Tick() { _count += 1; }
    public void Add(long amount) { checked { _total += amount; } }
}
""");
    }
}
