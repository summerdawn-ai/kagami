namespace Summerdawn.Kagami.Models;

/// <summary>
/// Splits supported filter expressions into atomic clauses.
/// </summary>
/// <remarks>
/// The parser recognizes only one flat level of case-insensitive <c>and</c> operators and
/// ignores matching text inside single-quoted string values.
/// </remarks>
internal static class FilterExpressionParser
{
    /// <summary>
    /// Splits an expression on top-level <c>and</c> operators.
    /// </summary>
    /// <param name="expression">The filter expression to split.</param>
    /// <returns>The trimmed atomic clauses in their original order.</returns>
    /// <exception cref="ArgumentException">Thrown when the expression contains an empty clause.</exception>
    public static IReadOnlyList<string> SplitAnd(string expression)
    {
        List<string> clauses = [];
        int clauseStart = 0;
        bool inString = false;

        for (int index = 0; index < expression.Length - 2; index++)
        {
            char character = expression[index];
            if (character == '\'')
            {
                if (inString && index + 1 < expression.Length && expression[index + 1] == '\'')
                {
                    index++;
                }
                else
                {
                    inString = !inString;
                }

                continue;
            }

            if (inString || !expression.AsSpan(index, 3).Equals("and", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool hasLeftBoundary = index > 0 && char.IsWhiteSpace(expression[index - 1]);
            bool hasRightBoundary = index + 3 < expression.Length && char.IsWhiteSpace(expression[index + 3]);
            if (!hasLeftBoundary || !hasRightBoundary)
            {
                continue;
            }

            AddClause(clauses, expression[clauseStart..index]);
            clauseStart = index + 3;
            index += 2;
        }

        AddClause(clauses, expression[clauseStart..]);
        return clauses;
    }

    /// <summary>
    /// Adds a non-empty trimmed clause to the parsed expression.
    /// </summary>
    private static void AddClause(List<string> clauses, string clause)
    {
        string trimmedClause = clause.Trim();
        if (trimmedClause.Length == 0)
        {
            throw new ArgumentException("Filter expressions cannot contain empty clauses.");
        }

        clauses.Add(trimmedClause);
    }
}
