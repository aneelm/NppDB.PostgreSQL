﻿using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using NppDB.Comm;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Contexts;
using System.Text;
using System.Threading.Tasks;
using static PostgreSQLParser;

namespace NppDB.PostgreSQL
{
    internal static class PostgreSQLAnalyzerHelper
    {

        public static bool In<T>(this T x, params T[] set)
        {
            return set.Contains(x);
        }

        public static void _AnalyzeIdentifier(PostgreSQLParser.IdentifierContext context, ParsedTreeCommand command)
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

        public static bool HasAggregateFunction(IParseTree context)
        {
            return HasSpecificAggregateFunction(context, "sum", "avg", "min", "max", "count");
        }

        public static bool HasSpecificAggregateFunction(IParseTree context, params string[] functionNames)
        {
            if (context is PostgreSQLParser.Func_applicationContext ctx &&
                ctx.func_name().GetText().ToLower().In(functionNames))
            {
                return true;
            }

            for (var n = 0; n < context.ChildCount; ++n)
            {
                var child = context.GetChild(n);
                var result = HasSpecificAggregateFunction(child, functionNames);
                if (result) return true;
            }
            return false;
        }

        public static bool HasAndOrExprWithoutParens(IParseTree context)
        {
            A_expr_orContext a_ExprOR = (A_expr_orContext)FindFirstTargetType(context, typeof(PostgreSQLParser.A_expr_orContext));

            while (a_ExprOR != null)
            {
                int ORCount = 0;
                int ANDCount = 0;
                ORCount += a_ExprOR.OR().Length;
                foreach (A_expr_andContext aExprAnd in a_ExprOR.a_expr_and())
                {
                    ANDCount += aExprAnd.AND().Length;
                }
                if (ORCount > 0 && ANDCount > 0) 
                {
                    return true;
                }
                a_ExprOR = (A_expr_orContext)FindFirstTargetType(a_ExprOR, typeof(PostgreSQLParser.A_expr_orContext));
            }
            return false;
        }

        public static void FindUsedOperands(IParseTree context, IList<IToken> results)
        {
            for (var n = 0; n < context.ChildCount; ++n)
            {
                var child = context.GetChild(n);
                if (DoesFieldExist(child, "_operands")) 
                {
                    object operandsValue = GetFieldValue(child, "_operands");
                    if (operandsValue is IList<IToken> operandsList)
                    {
                        foreach (IToken token in operandsList)
                        {
                            results.Add(token);
                        }
                    }
                }
                FindUsedOperands(child, results);
            }
        }

        public static object GetFieldValue(dynamic obj, string field)
        {
            System.Reflection.FieldInfo fieldInfo = ((Type)obj.GetType()).GetField(field);
            return fieldInfo?.GetValue(obj);
        }

        public static bool DoesFieldExist(dynamic obj, string field)
        {
            System.Reflection.FieldInfo[] fieldInfos = ((Type)obj.GetType()).GetFields();
            return fieldInfos.Where(p => p.Name.Equals(field)).Any();
        }

        public static bool HasParentOfAnyType(IParseTree context, IList<Type> breakIfReachTypes, IList<Type> targets) 
        {
            var parent = context.Parent;
            if (parent == null) 
            {
                return false;
            }
            foreach (Type target in breakIfReachTypes)
            {
                if (target.IsAssignableFrom(parent.GetType()))
                {
                    return false;
                }
            }
            foreach (Type target in targets)
            {
                if (target.IsAssignableFrom(parent.GetType()))
                {
                    return true;
                }
            }
            var result = HasParentOfAnyType(parent, breakIfReachTypes, targets);
            if (result)
            {
                return result;
            }
            return false;
        }

        public static bool IsLogicalExpression(IParseTree context)
        {
            IList<IToken> usedOperands = new List<IToken>();
            FindUsedOperands(context, usedOperands);
            int specialOperandForOperandsCount = usedOperands.Where(operand => operand.Type.In(BETWEEN)).Count();
            int specialOperandForCExprContextsCount = usedOperands.Where(operand => operand.Type.In(IN_P)).Count();

            IList<IParseTree> c_expr_Contexts = new List<IParseTree>();
            FindAllTargetTypes(context, typeof(C_exprContext), c_expr_Contexts);
            var breakTargets = new List<Type> { typeof(Where_clauseContext), typeof(From_clauseContext), typeof(Into_clauseContext), typeof(Having_clauseContext) };
            var targets = new List<Type> { typeof(Opt_target_listContext), typeof(Target_listContext), typeof(Sortby_listContext) };
            c_expr_Contexts = c_expr_Contexts.Where(ctx => 
                (((C_exprContext)ctx).Start.Type != OPEN_PAREN
                || ((C_exprContext)ctx).Stop.Type != CLOSE_PAREN)
                && !HasParentOfAnyType(ctx, breakTargets, targets))
            .ToList();
            return (c_expr_Contexts.Count + specialOperandForCExprContextsCount) - ((usedOperands.Count * 2) + specialOperandForOperandsCount) == 0;
        }

        public static bool IsSelectPramaryContextSelectStar(PostgreSQLParser.Simple_select_pramaryContext[] contexts)
        {
            if (contexts != null && contexts.Length > 0)
            {
                PostgreSQLParser.Simple_select_pramaryContext ctx = contexts[0];
                if (HasSelectStar(ctx))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool HasOuterJoin(PostgreSQLParser.Simple_select_pramaryContext context)
        {
            if (context.from_clause()?.from_list()?._tables != null && context.from_clause()?.from_list()?._tables.Count > 0)
            {
                foreach (Table_refContext table in context.from_clause()?.from_list()?._tables)
                {
                    if (table.CROSS().Length > 0)
                    {
                        return true;
                    }
                    foreach (Join_typeContext joinType in table.join_type())
                    {
                        if (joinType.GetText().ToLower().In("full", "left", "right", "outer"))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        //public static bool HasJoinButNoJoinQual(PostgreSQLParser.From_clauseContext context)
        //{
        //    if (context?.from_list()?._tables != null && context?.from_list()?._tables.Count > 0)
        //    {
        //        foreach (Table_refContext table in context?.from_list()?._tables)
        //        {
                    
        //            if (table.JOIN().Length > 0)
        //            {
        //                return true;
        //            }
        //        }
        //    }
        //    return false;
        //}

        public static bool HasSelectStar(Simple_select_pramaryContext ctx)
        {
            return ctx.opt_target_list()?.target_list()?.target_el() != null
                && ctx?.opt_target_list()?.target_list()?.target_el()?.Length > 0
                && ctx?.opt_target_list()?.target_list()?.target_el()[0] is PostgreSQLParser.Target_starContext;
        }

        public static IParseTree FindFirstTargetType(IParseTree context, Type target)
        {
            for (var n = 0; n < context.ChildCount; ++n)
            {
                var child = context.GetChild(n);
                if (target.IsAssignableFrom(child.GetType()))
                {
                    return child;
                }
                var result = FindFirstTargetType(child, target);
                if (result != null)
                {
                    return result;
                }
            }
            return null;
        }

        public static void FindAllTargetTypes(IParseTree context, Type target, IList<IParseTree> results)
        {
            for (var n = 0; n < context.ChildCount; ++n)
            {
                var child = context.GetChild(n);
                if (target.IsAssignableFrom(child.GetType()))
                {
                    results.Add(child);
                }
                FindAllTargetTypes(child, target, results);
            }
        }

        public static bool HasAExprConst(Sortby_listContext ctx)
        {
            foreach (SortbyContext sortBy in ctx.sortby())
            {
                var result = FindFirstTargetType(sortBy, typeof(AexprconstContext));
                if (result != null)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool HasGroupByClause(Simple_select_pramaryContext ctx)
        {
            return ctx.group_clause() != null && !string.IsNullOrEmpty(ctx.group_clause().GetText());
        }

        public static bool HasDistinctClause(Simple_select_pramaryContext ctx)
        {
            return ctx.distinct_clause() != null && !string.IsNullOrEmpty(ctx.distinct_clause().GetText());
        }

        public static bool HasText(IParseTree ctx)
        {
            return ctx != null && !string.IsNullOrEmpty(ctx.GetText());
        }

        public static int CountTablesInFromClause(Simple_select_pramaryContext ctx)
        {
            if (ctx.from_clause()?.from_list()?._tables != null) 
            {
                return CountTables(ctx.from_clause().from_list()._tables, 0);
            }
            return 0;
        }

        public static Target_elContext[] GetColumns(Simple_select_pramaryContext ctx)
        {
            if (ctx?.opt_target_list()?.target_list()?.target_el() != null) 
            {
                return ctx.opt_target_list().target_list().target_el();
            }
            return new Target_elContext[0];
        }

        public static int CountGroupingTerms(Simple_select_pramaryContext ctx)
        {
            if (ctx.group_clause()?.group_by_list()?._grouping_term != null)
            {
                return ctx.group_clause().group_by_list()._grouping_term.Count;
            }
            return 0;
        }

        public static int CountInsertColumns(InsertstmtContext ctx)
        {
            if (ctx?.insert_rest()?.insert_column_list()?._insert_columns != null)
            {
                return ctx.insert_rest().insert_column_list()._insert_columns.Count;
            }
            return 0;
        }

        public static bool HasDuplicateColumns(Target_elContext[] columns) 
        {
            List<String> columnNames = new List<String>();
            foreach (Target_elContext column in columns) 
            {
                if (column.ChildCount <= 1) 
                {
                    columnNames.Add(column.GetText());
                    continue;
                }
                columnNames.Add(column.GetChild(column.ChildCount - 1).GetText());
            }
            HashSet<String> columnNamesSet = new HashSet<String>(columnNames);
            return columnNames.Count != columnNamesSet.Count;
        }

        public static int CountWheres(IParseTree context, int count)
        {
            if (string.Equals(context.GetText(), "where", StringComparison.OrdinalIgnoreCase))
            {
                return count + 1;
            }
            for (var n = 0; n < context.ChildCount; ++n)
            {
                var child = context.GetChild(n);
                count = CountWheres(child, count);
            }
            return count;
        }

        public static int CountWhereClauses(IParseTree context)
        {
            IList<IParseTree> whereClauses = new List<IParseTree>();
            FindAllTargetTypes(context, typeof(Where_clauseContext), whereClauses);
            return whereClauses.Count;
        }

        public static bool ColumnHasAlias(Target_elContext column) 
        {
            return column != null && column.ChildCount > 1;
        }

        public static bool HasMissingColumnAlias(Target_elContext[] columns)
        {
            foreach (Target_elContext column in columns) 
            {
                if (!ColumnHasAlias(column)) 
                {
                    C_expr_exprContext value = (C_expr_exprContext) FindFirstTargetType(column, typeof(C_expr_exprContext));
                    if (value != null && value.ChildCount > 0)
                    {
                        if (value.GetChild(0) is AexprconstContext || value.GetChild(0) is Func_exprContext)
                        {
                            return true;
                        }
                    }
                }
                
            }
            return false;
        }

        public static Simple_select_pramaryContext FindSelectPramaryContext(IParseTree context) 
        {
            if (context != null)
            {
                if (context is Simple_select_pramaryContext ctx)
                {
                    return ctx;
                }
                for (var n = 0; n < context.ChildCount; ++n)
                {
                    var child = context.GetChild(n);
                    var result = FindSelectPramaryContext(child);
                    if (result is Simple_select_pramaryContext) 
                    {
                        return result;
                    }
                }
            }
            return null;
        }

        public static bool HasSubqueryColumnMismatch(A_expr_inContext a_expr_inContext, Simple_select_pramaryContext subQuery) 
        {
            Target_elContext[] subQueryColumns = GetColumns(subQuery);
            int subQueryColumnCount = subQueryColumns.Count();
            if (a_expr_inContext != null && a_expr_inContext.ChildCount > 1)
            {
                IList<IParseTree> c_expr_Contexts = new List<IParseTree>();
                FindAllTargetTypes(a_expr_inContext.a_expr_unary_not(), typeof(C_exprContext), c_expr_Contexts);
                c_expr_Contexts = c_expr_Contexts.Where(c_expr_ctx =>
                    ((C_exprContext)c_expr_ctx).Start.Type != OPEN_PAREN
                    || ((C_exprContext)c_expr_ctx).Stop.Type != CLOSE_PAREN)
                .ToList();
                if (c_expr_Contexts.Count != subQueryColumnCount)
                {
                    return true;
                }
            }
            return false;
        }

    }
}