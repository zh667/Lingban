using Lingban.Application.Common.Interfaces;

namespace Lingban.Infrastructure.Diagnostics;

public class QueryLog : IQueryLog
{
    private readonly List<string> _statements = new();

    public IReadOnlyList<string> Statements => _statements.AsReadOnly();

    public void Record(string statement) => _statements.Add(statement);

    public int Checkpoint() => _statements.Count;

    public IReadOnlyList<string> Since(int checkpoint)
    {
        int from = Math.Clamp(checkpoint, 0, _statements.Count);
        return _statements.Skip(from).ToList();
    }
}
