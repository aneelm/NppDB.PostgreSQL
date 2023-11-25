﻿using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using NppDB.Comm;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NppDB.PostgreSQL
{
    public static class PostgreSQLAnalyzer
    {
        public static int CollectCommands(
            this RuleContext context,
            CaretPosition caretPosition,
            string tokenSeparator,
            int commandSeparatorTokenType,
            out IList<ParsedTreeCommand> commands)
        {
            commands = new List<ParsedTreeCommand>();
            commands.Add(new ParsedTreeCommand
            {
                StartOffset = -1,
                StopOffset = -1,
                StartLine = -1,
                StopLine = -1,
                StartColumn = -1,
                StopColumn = -1,
                Text = "",
                Context = null
            });
            return _CollectCommands(context, caretPosition, tokenSeparator, commandSeparatorTokenType, commands, -1, null, new List<StringBuilder>());
        }

        private static void _AnalyzeIdentifier(PostgreSQLParser.IdentifierContext context, ParsedTreeCommand command)
        {
            if (context.GetText()[0] == '"' && context.GetText()[context.GetText().Length - 1] == '"')
            {
                command.AddWarning(context, ParserMessageType.DOUBLE_QUOTES);
            }
        }

        private static int CountTables(IList<PostgreSQLParser.Table_refContext> ctxs, int count)
        {
            if (ctxs != null && ctxs.Count > 0) 
            {
                foreach (var ctx in ctxs) 
                {
                    count++;
                    if (ctx._tables != null && ctx._tables.Count > 0) 
                    {
                        count = CountTables(ctx._tables, count);
                    }
                }
            }
            return count;
        }

        private static void _AnalyzeRuleContext(RuleContext context, ParsedTreeCommand command)
        {
            switch (context.RuleIndex)
            {
                case PostgreSQLParser.RULE_simple_select_pramary:
                    {
                        if (context is PostgreSQLParser.Simple_select_pramaryContext ctx)
                        {
                            if (ctx.distinct_clause() != null && ctx.group_clause() != null)
                            {
                                command.AddWarning(ctx, ParserMessageType.DISTINCT_KEYWORD_WITH_GROUP_BY_CLAUSE);
                            }
                            if (ctx.opt_target_list()?.target_list()?.target_el() != null 
                                && ctx.opt_target_list()?.target_list()?.target_el().Length > 0 
                                && ctx.opt_target_list()?.target_list()?.target_el()[0] is PostgreSQLParser.Target_starContext)
                            {
                                if (ctx.from_clause()?.from_list()?._tables != null && CountTables(ctx.from_clause().from_list()._tables, 0) > 1) 
                                {
                                    command.AddWarning(ctx, ParserMessageType.SELECT_ALL_WITH_MULTIPLE_JOINS);
                                }
                                
                            }
                        }
                        break;
                    }
                case PostgreSQLParser.RULE_select_clause: 
                    {
                        if (context is PostgreSQLParser.Select_clauseContext ctx)
                        {
                            //if (ctx.UNION != null)
                        }
                        break;
                    }
            }
        }

        private static int _CollectCommands(
            RuleContext context,
            CaretPosition caretPosition,
            string tokenSeparator,
            int commandSeparatorTokenType,
            in IList<ParsedTreeCommand> commands,
            int enclosingCommandIndex,
            in IList<StringBuilder> functionParams,
            in IList<StringBuilder> transactionStatementsEncountered)
        {
            if (context is PostgreSQLParser.RootContext && commands.Last().Context == null)
            {
                commands.Last().Context = context; // base starting branch
            }
            _AnalyzeRuleContext(context, commands.Last());
            for (var i = 0; i < context.ChildCount; ++i)
            {
                var child = context.GetChild(i);
                if (child is PostgreSQLParser.IdentifierContext identifier)
                {
                    _AnalyzeIdentifier(identifier, commands.Last());
                }
                if (child is ITerminalNode terminalNode)
                {
                    var token = terminalNode.Symbol;
                    var tokenLength = token.StopIndex - token.StartIndex + 1;
                    if (transactionStatementsEncountered?.Count % 2 == 0 && (token.Type == TokenConstants.EOF || token.Type == commandSeparatorTokenType))
                    {

                        if (enclosingCommandIndex == -1 && (
                            caretPosition.Line > commands.Last().StartLine && caretPosition.Line < token.Line ||
                            caretPosition.Line == commands.Last().StartLine && caretPosition.Column >= commands.Last().StartColumn && (caretPosition.Line < token.Line || caretPosition.Column <= token.Column) ||
                            caretPosition.Line == token.Line && caretPosition.Column <= token.Column))
                        {
                            enclosingCommandIndex = commands.Count - 1;
                        }

                        if (token.Type == TokenConstants.EOF)
                        {
                            continue;
                        }
                        commands.Add(new ParsedTreeCommand());
                    }
                    else
                    {
                        if (commands.Last().StartOffset == -1)
                        {
                            commands.Last().StartLine = token.Line;
                            commands.Last().StartColumn = token.Column;
                            commands.Last().StartOffset = token.StartIndex;
                        }
                        commands.Last().StopLine = token.Line;
                        commands.Last().StopColumn = token.Column + tokenLength;
                        commands.Last().StopOffset = token.StopIndex;

                        if (functionParams is null)
                        {
                            commands.Last().Text = commands.Last().Text + child.GetText() + tokenSeparator;
                        }
                        else if (context.RuleIndex != PostgreSQLParser.RULE_function_or_procedure || token.Type != PostgreSQLParser.OPEN_PAREN && token.Type != PostgreSQLParser.CLOSE_PAREN && token.Type != PostgreSQLParser.COMMA)
                        {
                            functionParams.Last().Append(child.GetText() + tokenSeparator);
                        }
                    }
                }
                else
                {
                    var ctx = child as RuleContext;


                    if (ctx?.RuleIndex == PostgreSQLParser.RULE_transactionstmt)
                    {
                        var statements = new string[] { "BEGIN", "END" };
                        if (statements.Any(s => ctx?.GetText().IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            transactionStatementsEncountered?.Add(new StringBuilder(ctx?.GetText()));
                        }
                    };


                    if (ctx?.RuleIndex == PostgreSQLParser.RULE_empty_grouping_set) continue;

                    if (ctx?.RuleIndex == PostgreSQLParser.RULE_indirection_el)
                    {
                        enclosingCommandIndex = _CollectCommands(ctx, caretPosition, "", commandSeparatorTokenType, commands, enclosingCommandIndex, functionParams, transactionStatementsEncountered);
                        if (functionParams is null)
                            commands.Last().Text += tokenSeparator;
                        else
                            functionParams.Last().Append(tokenSeparator);
                    }
                    else if (ctx?.RuleIndex == PostgreSQLParser.RULE_ruleactionlist)
                    {
                        var p = new List<StringBuilder> { new StringBuilder() };
                        enclosingCommandIndex = _CollectCommands(ctx, caretPosition, tokenSeparator, commandSeparatorTokenType, commands, enclosingCommandIndex, p, transactionStatementsEncountered);
                        var functionName = p[0].ToString().ToLower();
                        p.RemoveAt(0);
                        var functionCallString = "";
                        if (functionName == "nz" && (p.Count == 2 || p.Count == 3))
                        {
                            if (p[1].Length == 0) p[1].Append("''");
                            functionCallString = $"IIf(IsNull({p[0]}), {p[1]}, {p[0]})";
                        }
                        else
                        {
                            functionCallString = $"{functionName}({string.Join(", ", p).TrimEnd(',', ' ')})";
                        }

                        if (functionParams is null)
                        {
                            commands.Last().Text = commands.Last().Text + functionCallString + tokenSeparator;
                        }
                        else
                        {
                            functionParams.Last().Append(functionCallString + tokenSeparator);
                        }
                    }
                    else
                    {
                        enclosingCommandIndex = _CollectCommands(ctx, caretPosition, tokenSeparator, commandSeparatorTokenType, commands, enclosingCommandIndex, functionParams, transactionStatementsEncountered);
                        if (!(functionParams is null) && (ctx?.RuleIndex == PostgreSQLParser.RULE_colid || ctx?.RuleIndex == PostgreSQLParser.RULE_stmtblock))
                        {
                            functionParams.Last().Length -= tokenSeparator.Length;
                            functionParams.Add(new StringBuilder());
                        }
                    }
                }
            }
            return enclosingCommandIndex;
        }
    }
}
