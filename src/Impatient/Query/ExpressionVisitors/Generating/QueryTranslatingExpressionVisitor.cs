﻿using Impatient.Query.Expressions;
using Impatient.Query.ExpressionVisitors.Utility;
using Impatient.Query.Infrastructure;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Impatient.Query.ExpressionVisitors.Generating
{
    public class QueryTranslatingExpressionVisitor : ExpressionVisitor
    {
        private readonly IComplexTypeSubqueryFormatter complexTypeSubqueryFormatter;
        private readonly HashSet<string> tableAliases = new HashSet<string>();
        private readonly IDictionary<AliasedTableExpression, string> aliasLookup = new Dictionary<AliasedTableExpression, string>();

        public QueryTranslatingExpressionVisitor(
            IDbCommandExpressionBuilder dbCommandExpressionBuilder,
            IComplexTypeSubqueryFormatter complexTypeSubqueryFormatter)
        {
            Builder = dbCommandExpressionBuilder;
            this.complexTypeSubqueryFormatter = complexTypeSubqueryFormatter;
        }

        protected IDbCommandExpressionBuilder Builder { get; }

        public LambdaExpression Translate(SelectExpression selectExpression)
        {
            Visit(selectExpression);

            return Builder.Build();
        }

        #region Logical overrides

        public override Expression Visit(Expression node)
        {
            switch (node)
            {
                case BinaryExpression binaryExpression:
                {
                    return VisitBinary(binaryExpression);
                }

                case ConditionalExpression conditionalExpression:
                {
                    return VisitConditional(conditionalExpression);
                }

                case ConstantExpression constantExpression:
                {
                    return VisitConstant(constantExpression);
                }

                case UnaryExpression unaryExpression:
                {
                    return VisitUnary(unaryExpression);
                }

                default:
                {
                    if (node.NodeType == ExpressionType.Extension)
                    {
                        return VisitExtension(node);
                    }

                    throw new NotSupportedException();
                }
            }
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            Expression VisitBinaryOperand(Expression operand)
            {
                switch (operand)
                {
                    case BinaryExpression binaryExpression:
                    {
                        Builder.Append("(");

                        operand = VisitBinary(binaryExpression);

                        Builder.Append(")");

                        break;
                    }

                    default:
                    {
                        operand = Visit(operand);

                        break;
                    }
                }

                return operand;
            }

            Expression VisitSimple(string @operator)
            {
                var left = VisitBinaryOperand(node.Left);

                Builder.Append(@operator);

                var right = VisitBinaryOperand(node.Right);

                return node.Update(left, node.Conversion, right);
            }

            Expression PrepareEqualityOperand(Expression operand)
            {
                return operand.ReplaceWithConversions(inner =>
                {
                    switch (inner.NodeType)
                    {
                        case ExpressionType.Not when inner.Type.IsBooleanType():
                        {
                            inner = ((UnaryExpression)inner).Operand;

                            if (inner.Type.IsNullableType())
                            {
                                inner = Expression.Coalesce(inner, Expression.Constant(true));
                            }

                            return Expression.Condition(inner, Expression.Constant(false), Expression.Constant(true));
                        }

                        case ExpressionType.Equal:
                        case ExpressionType.NotEqual:
                        case ExpressionType.GreaterThan:
                        case ExpressionType.GreaterThanOrEqual:
                        case ExpressionType.LessThan:
                        case ExpressionType.LessThanOrEqual:
                        case ExpressionType.And when inner.Type.IsBooleanType():
                        case ExpressionType.Or when inner.Type.IsBooleanType():
                        {
                            if (inner.Type.IsNullableType())
                            {
                                inner = Expression.Coalesce(inner, Expression.Constant(false));
                            }

                            return Expression.Condition(inner, Expression.Constant(true), Expression.Constant(false));
                        }

                        default:
                        {
                            return inner;
                        }
                    }
                });
            }

            switch (node.NodeType)
            {
                case ExpressionType.Coalesce:
                {
                    Builder.Append("COALESCE(");

                    var left = Visit(node.Left);

                    Builder.Append(", ");

                    var right = Visit(node.Right);

                    Builder.Append(")");

                    return node.Update(left, node.Conversion, right);
                }

                case ExpressionType.AndAlso:
                case ExpressionType.And when node.Type.IsBooleanType():
                {
                    var left = VisitBinaryOperand(node.Left.AsSqlBooleanExpression());

                    Builder.Append(" AND ");

                    var right = VisitBinaryOperand(node.Right.AsSqlBooleanExpression());

                    return node.Update(left, node.Conversion, right);
                }

                case ExpressionType.OrElse:
                case ExpressionType.Or when node.Type.IsBooleanType():
                {
                    var left = VisitBinaryOperand(node.Left.AsSqlBooleanExpression());

                    Builder.Append(" OR ");

                    var right = VisitBinaryOperand(node.Right.AsSqlBooleanExpression());

                    return node.Update(left, node.Conversion, right);
                }

                case ExpressionType.Equal:
                {
                    var left = PrepareEqualityOperand(node.Left);
                    var right = PrepareEqualityOperand(node.Right);

                    if (left is NewExpression leftNewExpression
                        && right is NewExpression rightNewExpression)
                    {
                        return Visit(
                            leftNewExpression.Arguments
                                .Zip(rightNewExpression.Arguments, Expression.Equal)
                                .Aggregate(Expression.AndAlso)
                                .Balance());
                    }
                    else if (left is MemberInitExpression leftMemberInitExpression
                        && right is MemberInitExpression rightMemberInitExpression)
                    {
                        var leftBindings
                            = leftMemberInitExpression.NewExpression.Arguments.Concat(
                                leftMemberInitExpression.Bindings.Iterate()
                                    .Cast<MemberAssignment>().Select(m => m.Expression));

                        var rightBindings
                            = rightMemberInitExpression.NewExpression.Arguments.Concat(
                                rightMemberInitExpression.Bindings.Iterate()
                                    .Cast<MemberAssignment>().Select(m => m.Expression));

                        return Visit(
                            leftBindings
                                .Zip(rightBindings, Expression.Equal)
                                .Aggregate(Expression.AndAlso)
                                .Balance());
                    }
                    else if (left is NewArrayExpression leftNewArrayExpression
                        && right is NewArrayExpression rightNewArrayExpression)
                    {
                        return Visit(
                            leftNewArrayExpression.Expressions
                                .Zip(rightNewArrayExpression.Expressions, Expression.Equal)
                                .Aggregate(Expression.AndAlso)
                                .Balance());
                    }
                    else if (left is ConstantExpression leftConstantExpression
                        && leftConstantExpression.Value is null)
                    {
                        right = Visit(right);

                        Builder.Append(" IS NULL");

                        return node.Update(left, node.Conversion, right);
                    }
                    else if (right is ConstantExpression rightConstantExpression
                        && rightConstantExpression.Value is null)
                    {
                        left = Visit(left);

                        Builder.Append(" IS NULL");

                        return node.Update(left, node.Conversion, right);
                    }

                    var leftIsNullable
                        = left.UnwrapAnnotationsAndConversions() is SqlExpression leftSqlExpression
                            && leftSqlExpression.IsNullable;

                    var rightIsNullable
                        = right.UnwrapAnnotationsAndConversions() is SqlExpression rightSqlExpression
                            && rightSqlExpression.IsNullable;

                    if (leftIsNullable && rightIsNullable)
                    {
                        Builder.Append("((");
                        Visit(left);
                        Builder.Append(" IS NULL AND ");
                        Visit(right);
                        Builder.Append(" IS NULL) OR (");
                        left = Visit(left);
                        Builder.Append(" = ");
                        right = Visit(right);
                        Builder.Append("))");

                        return node.Update(left, node.Conversion, right);
                    }

                    left = VisitBinaryOperand(left);

                    Builder.Append(" = ");

                    right = VisitBinaryOperand(right);

                    return node.Update(left, node.Conversion, right);
                }

                case ExpressionType.NotEqual:
                {
                    var left = PrepareEqualityOperand(node.Left);
                    var right = PrepareEqualityOperand(node.Right);

                    if (left is NewExpression leftNewExpression
                        && right is NewExpression rightNewExpression)
                    {
                        return Visit(
                            leftNewExpression.Arguments
                                .Zip(rightNewExpression.Arguments, Expression.NotEqual)
                                .Aggregate(Expression.OrElse));
                    }
                    else if (left is MemberInitExpression leftMemberInitExpression
                        && right is MemberInitExpression rightMemberInitExpression)
                    {
                        var leftBindings
                            = leftMemberInitExpression.NewExpression.Arguments.Concat(
                                leftMemberInitExpression.Bindings.Iterate()
                                    .Cast<MemberAssignment>().Select(m => m.Expression));

                        var rightBindings
                            = rightMemberInitExpression.NewExpression.Arguments.Concat(
                                rightMemberInitExpression.Bindings.Iterate()
                                    .Cast<MemberAssignment>().Select(m => m.Expression));

                        return Visit(
                            leftBindings
                                .Zip(rightBindings, Expression.NotEqual)
                                .Aggregate(Expression.OrElse));
                    }
                    else if (left is NewArrayExpression leftNewArrayExpression
                        && right is NewArrayExpression rightNewArrayExpression)
                    {
                        return Visit(
                            leftNewArrayExpression.Expressions
                                .Zip(rightNewArrayExpression.Expressions, Expression.NotEqual)
                                .Aggregate(Expression.OrElse)
                                .Balance());
                    }
                    else if (left is ConstantExpression leftConstantExpression
                        && leftConstantExpression.Value is null)
                    {
                        right = Visit(right);

                        Builder.Append(" IS NOT NULL");

                        return node.Update(left, node.Conversion, right);
                    }
                    else if (right is ConstantExpression rightConstantExpression
                        && rightConstantExpression.Value is null)
                    {
                        left = Visit(left);

                        Builder.Append(" IS NOT NULL");

                        return node.Update(left, node.Conversion, right);
                    }

                    var leftIsNullable
                        = left.UnwrapAnnotationsAndConversions() is SqlExpression leftSqlExpression
                            && leftSqlExpression.IsNullable;

                    var rightIsNullable
                        = right.UnwrapAnnotationsAndConversions() is SqlExpression rightSqlExpression
                            && rightSqlExpression.IsNullable;

                    if (leftIsNullable && rightIsNullable)
                    {
                        Builder.Append("((");
                        Visit(left);
                        Builder.Append(" IS NULL AND ");
                        Visit(right);
                        Builder.Append(" IS NOT NULL) OR (");
                        Visit(left);
                        Builder.Append(" IS NOT NULL AND ");
                        Visit(right);
                        Builder.Append(" IS NULL) OR (");
                        left = Visit(left);
                        Builder.Append(" <> ");
                        right = Visit(right);
                        Builder.Append("))");

                        return node.Update(left, node.Conversion, right);
                    }
                    else if (leftIsNullable)
                    {
                        Builder.Append("(");
                        Visit(left);
                        Builder.Append(" IS NULL OR (");
                        left = Visit(left);
                        Builder.Append(" <> ");
                        right = Visit(right);
                        Builder.Append("))");

                        return node.Update(left, node.Conversion, right);
                    }
                    else if (rightIsNullable)
                    {
                        Builder.Append("(");
                        Visit(right);
                        Builder.Append(" IS NULL OR (");
                        left = Visit(left);
                        Builder.Append(" <> ");
                        right = Visit(right);
                        Builder.Append("))");

                        return node.Update(left, node.Conversion, right);
                    }

                    left = VisitBinaryOperand(left);

                    Builder.Append(" <> ");

                    right = VisitBinaryOperand(right);

                    return node.Update(left, node.Conversion, right);
                }

                case ExpressionType.GreaterThan:
                {
                    return VisitSimple(" > ");
                }

                case ExpressionType.GreaterThanOrEqual:
                {
                    return VisitSimple(" >= ");
                }

                case ExpressionType.LessThan:
                {
                    return VisitSimple(" < ");
                }

                case ExpressionType.LessThanOrEqual:
                {
                    return VisitSimple(" <= ");
                }

                case ExpressionType.Add:
                {
                    return VisitSimple(" + ");
                }

                case ExpressionType.Subtract:
                {
                    return VisitSimple(" - ");
                }

                case ExpressionType.Multiply:
                {
                    return VisitSimple(" * ");
                }

                case ExpressionType.Divide:
                {
                    return VisitSimple(" / ");
                }

                case ExpressionType.Modulo:
                {
                    return VisitSimple(" % ");
                }

                case ExpressionType.And:
                {
                    return VisitSimple(" & ");
                }

                case ExpressionType.Or:
                {
                    return VisitSimple(" | ");
                }

                case ExpressionType.ExclusiveOr:
                {
                    return VisitSimple(" ^ ");
                }

                default:
                {
                    throw new NotSupportedException();
                }
            }
        }

        protected override Expression VisitConditional(ConditionalExpression node)
        {
            Builder.Append("(CASE WHEN ");

            var test = Visit(node.Test.AsSqlBooleanExpression());

            Builder.Append(" THEN ");

            var ifTrue = node.IfTrue;

            if (ifTrue is BinaryExpression && ifTrue.Type.IsBooleanType())
            {
                ifTrue = Visit(Expression.Condition(ifTrue, Expression.Constant(true), Expression.Constant(false)));
            }
            else
            {
                ifTrue = Visit(ifTrue);
            }

            Builder.Append(" ELSE ");

            var ifFalse = node.IfFalse;

            if (ifFalse is BinaryExpression && ifFalse.Type.IsBooleanType())
            {
                ifFalse = Visit(Expression.Condition(ifFalse, Expression.Constant(true), Expression.Constant(false)));
            }
            else
            {
                ifFalse = Visit(ifFalse);
            }

            Builder.Append(" END)");

            return node.Update(test, ifTrue, ifFalse);
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            switch (node.Value)
            {
                case string value:
                {
                    Builder.Append($@"N'{value.Replace("'", "''")}'");

                    return node;
                }

                case char value:
                {
                    Builder.Append($@"N'{value}'");

                    return node;
                }

                case bool value:
                {
                    Builder.Append(value ? "1" : "0");

                    return node;
                }

                case double value:
                {
                    Builder.Append(value.ToString("G17"));

                    return node;
                }

                case decimal value:
                {
                    Builder.Append(value.ToString("0.0###########################"));

                    return node;
                }

                case DateTime value:
                {
                    Builder.Append($"'{value.ToString("yyyy-MM-ddTHH:mm:ss.fffK")}'");

                    return node;
                }

                case Enum value:
                {
                    Builder.Append(Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType())).ToString());

                    return node;
                }

                case object value:
                {
                    Builder.Append(value.ToString());

                    return node;
                }

                case null:
                {
                    Builder.Append("NULL");

                    return node;
                }

                default:
                {
                    throw new NotSupportedException();
                }
            }
        }

        protected override Expression VisitExtension(Expression node)
        {
            switch (node)
            {
                case SelectExpression selectExpression:
                {
                    return VisitSelectExpression(selectExpression);
                }

                case SingleValueRelationalQueryExpression singleValueRelationalQueryExpression:
                {
                    return VisitSingleValueRelationalQueryExpression(singleValueRelationalQueryExpression);
                }

                case EnumerableRelationalQueryExpression enumerableRelationalQueryExpression:
                {
                    return VisitEnumerableRelationalQueryExpression(enumerableRelationalQueryExpression);
                }

                case BaseTableExpression baseTableExpression:
                {
                    return VisitBaseTableExpression(baseTableExpression);
                }

                case SubqueryTableExpression subqueryTableExpression:
                {
                    return VisitSubqueryTableExpression(subqueryTableExpression);
                }

                case InnerJoinExpression innerJoinExpression:
                {
                    return VisitInnerJoinExpression(innerJoinExpression);
                }

                case LeftJoinExpression leftJoinExpression:
                {
                    return VisitLeftJoinExpression(leftJoinExpression);
                }

                case FullJoinExpression fullJoinExpression:
                {
                    return VisitFullJoinExpression(fullJoinExpression);
                }

                case CrossJoinExpression crossJoinExpression:
                {
                    return VisitCrossJoinExpression(crossJoinExpression);
                }

                case CrossApplyExpression crossApplyExpression:
                {
                    return VisitCrossApplyExpression(crossApplyExpression);
                }

                case OuterApplyExpression outerApplyExpression:
                {
                    return VisitOuterApplyExpression(outerApplyExpression);
                }

                case SetOperatorExpression setOperatorExpression:
                {
                    return VisitSetOperatorExpression(setOperatorExpression);
                }

                case SqlAggregateExpression sqlAggregateExpression:
                {
                    return VisitSqlAggregateExpression(sqlAggregateExpression);
                }

                case SqlAliasExpression sqlAliasExpression:
                {
                    return VisitSqlAliasExpression(sqlAliasExpression);
                }

                case SqlCastExpression sqlCastExpression:
                {
                    return VisitSqlCastExpression(sqlCastExpression);
                }

                case SqlColumnExpression sqlColumnExpression:
                {
                    return VisitSqlColumnExpression(sqlColumnExpression);
                }

                case SqlConcatExpression sqlConcatExpression:
                {
                    return VisitSqlConcatExpression(sqlConcatExpression);
                }

                case SqlExistsExpression sqlExistsExpression:
                {
                    return VisitSqlExistsExpression(sqlExistsExpression);
                }

                case SqlFragmentExpression sqlFragmentExpression:
                {
                    return VisitSqlFragmentExpression(sqlFragmentExpression);
                }

                case SqlFunctionExpression sqlFunctionExpression:
                {
                    return VisitSqlFunctionExpression(sqlFunctionExpression);
                }

                case SqlInExpression sqlInExpression:
                {
                    return VisitSqlInExpression(sqlInExpression);
                }

                case SqlParameterExpression sqlParameterExpression:
                {
                    return VisitSqlParameterExpression(sqlParameterExpression);
                }

                case SqlWindowFunctionExpression sqlWindowFunctionExpression:
                {
                    return VisitSqlWindowFunctionExpression(sqlWindowFunctionExpression);
                }

                case OrderByExpression orderByExpression:
                {
                    return VisitOrderByExpression(orderByExpression);
                }

                default:
                {
                    return base.VisitExtension(node);
                }
            }
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            switch (node.NodeType)
            {
                case ExpressionType.Not
                when node.Type.IsBooleanType():
                {
                    switch (node.Operand)
                    {
                        case SqlExistsExpression sqlExistsExpression:
                        case SqlInExpression sqlInExpression:
                        {
                            Builder.Append("NOT ");

                            return base.VisitUnary(node);
                        }

                        default:
                        {
                            // TODO: Handle nullable operand
                            return base.Visit(Expression.Equal(Expression.Constant(false), node.Operand));
                        }
                    }
                }

                case ExpressionType.Not:
                {
                    Builder.Append("~ ");

                    return base.VisitUnary(node);
                }

                case ExpressionType.Convert:
                {
                    return base.VisitUnary(node);
                }

                default:
                {
                    throw new NotSupportedException();
                }
            }
        }

        #endregion

        #region Extension Expression visiting methods

        protected virtual Expression VisitSelectExpression(SelectExpression selectExpression)
        {
            Builder.Append("SELECT ");

            if (selectExpression.IsDistinct)
            {
                Builder.Append("DISTINCT ");
            }

            if (selectExpression.Limit != null && selectExpression.Offset == null)
            {
                Builder.Append("TOP (");

                Visit(selectExpression.Limit);

                Builder.Append(") ");
            }

            var projectionExpressions
                = FlattenProjection(selectExpression.Projection)
                    .Select((e, i) => (i, e.alias, e.expression));

            foreach (var (index, alias, expression) in projectionExpressions)
            {
                if (index > 0)
                {
                    Builder.Append(", ");
                }

                if (expression.Type.IsBooleanType()
                    && !(expression is SqlAliasExpression
                       || expression is SqlColumnExpression
                       || expression is SqlCastExpression))
                {
                    Builder.Append("CAST(");

                    EmitExpressionListExpression(expression);

                    Builder.Append(" AS BIT)");
                }
                else
                {
                    EmitExpressionListExpression(expression);
                }

                if (!string.IsNullOrEmpty(alias))
                {
                    Builder.Append(" AS ");
                    Builder.Append(FormatIdentifier(alias));
                }
            }

            if (selectExpression.Table != null)
            {
                Builder.AppendLine();
                Builder.Append("FROM ");

                Visit(selectExpression.Table);
            }

            if (selectExpression.Predicate != null)
            {
                Builder.AppendLine();
                Builder.Append("WHERE ");

                Visit(selectExpression.Predicate.AsSqlBooleanExpression());
            }

            if (selectExpression.Grouping != null)
            {
                Builder.AppendLine();
                Builder.Append("GROUP BY ");

                var groupings = FlattenProjection(selectExpression.Grouping);

                EmitExpressionListExpression(groupings.First().expression);

                foreach (var grouping in groupings.Skip(1))
                {
                    Builder.Append(", ");

                    EmitExpressionListExpression(grouping.expression);
                }
            }

            if (selectExpression.OrderBy != null)
            {
                Builder.AppendLine();
                Builder.Append("ORDER BY ");

                Visit(selectExpression.OrderBy);
            }

            if (selectExpression.Offset != null)
            {
                Builder.AppendLine();
                Builder.Append("OFFSET ");

                Visit(selectExpression.Offset);

                Builder.Append(" ROWS");

                if (selectExpression.Limit != null)
                {
                    Builder.Append(" FETCH NEXT ");

                    Visit(selectExpression.Limit);

                    Builder.Append(" ROWS ONLY");
                }
            }

            return selectExpression;
        }

        protected virtual Expression VisitSingleValueRelationalQueryExpression(SingleValueRelationalQueryExpression singleValueRelationalQueryExpression)
        {
            if (!singleValueRelationalQueryExpression.Type.IsScalarType())
            {
                complexTypeSubqueryFormatter.Format(
                    singleValueRelationalQueryExpression.SelectExpression,
                    Builder,
                    this);
            }
            else if (singleValueRelationalQueryExpression == SingleValueRelationalQueryExpression.SelectOne)
            {
                Builder.Append("(SELECT 1)");
            }
            else
            {
                Builder.Append("(");

                Builder.IncreaseIndent();
                Builder.AppendLine();

                Visit(singleValueRelationalQueryExpression.SelectExpression);

                Builder.DecreaseIndent();
                Builder.AppendLine();

                Builder.Append(")");
            }

            return singleValueRelationalQueryExpression;
        }

        protected virtual Expression VisitEnumerableRelationalQueryExpression(EnumerableRelationalQueryExpression enumerableRelationalQueryExpression)
        {
            complexTypeSubqueryFormatter.Format(
                enumerableRelationalQueryExpression.SelectExpression,
                Builder,
                this);

            return enumerableRelationalQueryExpression;
        }

        protected virtual Expression VisitBaseTableExpression(BaseTableExpression baseTableExpression)
        {
            if (!string.IsNullOrEmpty(baseTableExpression.SchemaName))
            {
                Builder.Append(FormatIdentifier(baseTableExpression.SchemaName));
                Builder.Append(".");
            }

            Builder.Append(FormatIdentifier(baseTableExpression.TableName));
            Builder.Append(" AS ");
            Builder.Append(FormatIdentifier(GetTableAlias(baseTableExpression)));

            return baseTableExpression;
        }

        protected virtual Expression VisitSubqueryTableExpression(SubqueryTableExpression subqueryTableExpression)
        {
            Builder.Append("(");

            Builder.IncreaseIndent();
            Builder.AppendLine();

            Visit(subqueryTableExpression.Subquery);

            Builder.DecreaseIndent();
            Builder.AppendLine();

            Builder.Append(") AS ");
            Builder.Append(FormatIdentifier(GetTableAlias(subqueryTableExpression)));

            return subqueryTableExpression;
        }

        protected virtual Expression VisitInnerJoinExpression(InnerJoinExpression innerJoinExpression)
        {
            Visit(innerJoinExpression.OuterTable);

            Builder.AppendLine();
            Builder.Append("INNER JOIN ");

            Visit(innerJoinExpression.InnerTable);

            Builder.Append(" ON ");

            Visit(innerJoinExpression.Predicate.AsSqlBooleanExpression());

            return innerJoinExpression;
        }

        protected virtual Expression VisitLeftJoinExpression(LeftJoinExpression leftJoinExpression)
        {
            Visit(leftJoinExpression.OuterTable);

            Builder.AppendLine();
            Builder.Append("LEFT JOIN ");

            Visit(leftJoinExpression.InnerTable);

            Builder.Append(" ON ");

            Visit(leftJoinExpression.Predicate.AsSqlBooleanExpression());

            return leftJoinExpression;
        }

        protected virtual Expression VisitFullJoinExpression(FullJoinExpression fullJoinExpression)
        {
            Visit(fullJoinExpression.OuterTable);

            Builder.AppendLine();
            Builder.Append("FULL JOIN ");

            Visit(fullJoinExpression.InnerTable);

            Builder.Append(" ON ");

            Visit(fullJoinExpression.Predicate.AsSqlBooleanExpression());

            return fullJoinExpression;
        }

        protected virtual Expression VisitCrossJoinExpression(CrossJoinExpression crossJoinExpression)
        {
            Visit(crossJoinExpression.OuterTable);

            Builder.AppendLine();
            Builder.Append("CROSS JOIN ");

            Visit(crossJoinExpression.InnerTable);

            return crossJoinExpression;
        }

        protected virtual Expression VisitCrossApplyExpression(CrossApplyExpression crossApplyExpression)
        {
            Visit(crossApplyExpression.OuterTable);

            Builder.AppendLine();
            Builder.Append("CROSS APPLY ");

            Visit(crossApplyExpression.InnerTable);

            return crossApplyExpression;
        }

        protected virtual Expression VisitOuterApplyExpression(OuterApplyExpression outerApplyExpression)
        {
            Visit(outerApplyExpression.OuterTable);

            Builder.AppendLine();
            Builder.Append("OUTER APPLY ");

            Visit(outerApplyExpression.InnerTable);

            return outerApplyExpression;
        }

        protected virtual Expression VisitSetOperatorExpression(SetOperatorExpression setOperatorExpression)
        {
            Builder.Append("(");

            Builder.IncreaseIndent();
            Builder.AppendLine();

            Visit(setOperatorExpression.Set1);

            Builder.AppendLine();

            switch (setOperatorExpression)
            {
                case ExceptExpression exceptExpression:
                {
                    Builder.Append("EXCEPT");
                    break;
                }

                case IntersectExpression intersectExpression:
                {
                    Builder.Append("INTERSECT");
                    break;
                }

                case UnionAllExpression unionAllExpression:
                {
                    Builder.Append("UNION ALL");
                    break;
                }

                case UnionExpression unionExpression:
                {
                    Builder.Append("UNION");
                    break;
                }

                default:
                {
                    throw new NotSupportedException();
                }
            }

            Builder.AppendLine();

            Visit(setOperatorExpression.Set2);

            Builder.DecreaseIndent();
            Builder.AppendLine();

            Builder.Append(") AS ");
            Builder.Append(FormatIdentifier(GetTableAlias(setOperatorExpression)));

            return setOperatorExpression;
        }

        protected virtual Expression VisitSqlAggregateExpression(SqlAggregateExpression sqlAggregateExpression)
        {
            Builder.Append(sqlAggregateExpression.FunctionName);
            Builder.Append("(");

            if (sqlAggregateExpression.IsDistinct)
            {
                Builder.Append("DISTINCT ");
            }

            Visit(sqlAggregateExpression.Expression);

            Builder.Append(")");

            return sqlAggregateExpression;
        }

        protected virtual Expression VisitSqlAliasExpression(SqlAliasExpression sqlAliasExpression)
        {
            Visit(sqlAliasExpression.Expression);

            Builder.Append(" AS ");
            Builder.Append(FormatIdentifier(sqlAliasExpression.Alias));

            return sqlAliasExpression;
        }

        protected virtual Expression VisitSqlCastExpression(SqlCastExpression sqlCastExpression)
        {
            Builder.Append("CAST(");

            Visit(sqlCastExpression.Expression);

            Builder.Append($" AS {sqlCastExpression.SqlType})");

            return sqlCastExpression;
        }

        protected virtual Expression VisitSqlColumnExpression(SqlColumnExpression sqlColumnExpression)
        {
            Builder.Append(FormatIdentifier(GetTableAlias(sqlColumnExpression.Table)));
            Builder.Append(".");
            Builder.Append(FormatIdentifier(sqlColumnExpression.ColumnName));

            return sqlColumnExpression;
        }

        protected virtual Expression VisitSqlConcatExpression(SqlConcatExpression sqlConcatExpression)
        {
            Visit(sqlConcatExpression.Segments.First());

            foreach (var segment in sqlConcatExpression.Segments.Skip(1))
            {
                Builder.Append(" + ");

                Visit(segment);
            }

            return sqlConcatExpression;
        }

        protected virtual Expression VisitSqlExistsExpression(SqlExistsExpression sqlExistsExpression)
        {
            Builder.Append("EXISTS (");

            Builder.IncreaseIndent();
            Builder.AppendLine();

            Visit(sqlExistsExpression.SelectExpression);

            Builder.DecreaseIndent();
            Builder.AppendLine();

            Builder.Append(")");

            return sqlExistsExpression;
        }

        protected virtual Expression VisitSqlFragmentExpression(SqlFragmentExpression sqlFragmentExpression)
        {
            Builder.Append(sqlFragmentExpression.Fragment);

            return sqlFragmentExpression;
        }

        protected virtual Expression VisitSqlFunctionExpression(SqlFunctionExpression sqlFunctionExpression)
        {
            Builder.Append(sqlFunctionExpression.FunctionName);
            Builder.Append("(");

            if (sqlFunctionExpression.Arguments.Any())
            {
                Visit(sqlFunctionExpression.Arguments.First());

                foreach (var argument in sqlFunctionExpression.Arguments.Skip(1))
                {
                    Builder.Append(", ");

                    Visit(argument);
                }
            }

            Builder.Append(")");

            return sqlFunctionExpression;
        }

        protected virtual Expression VisitSqlInExpression(SqlInExpression sqlInExpression)
        {
            Visit(sqlInExpression.Value);

            Builder.Append(" IN (");

            var handled = false;

            switch (sqlInExpression.Values)
            {
                case RelationalQueryExpression relationalQueryExpression:
                {
                    Builder.IncreaseIndent();
                    Builder.AppendLine();

                    Visit(relationalQueryExpression.SelectExpression);

                    Builder.DecreaseIndent();
                    Builder.AppendLine();

                    handled = true;

                    break;
                }

                case SelectExpression selectExpression:
                {
                    Builder.IncreaseIndent();
                    Builder.AppendLine();

                    Visit(selectExpression);

                    Builder.DecreaseIndent();
                    Builder.AppendLine();

                    handled = true;

                    break;
                }

                case NewArrayExpression newArrayExpression:
                {
                    foreach (var (expression, index) in newArrayExpression.Expressions.Select((e, i) => (e, i)))
                    {
                        handled = true;

                        if (index > 0)
                        {
                            Builder.Append(", ");
                        }

                        Visit(expression);
                    }

                    break;
                }

                case ListInitExpression listInitExpression:
                {
                    foreach (var (elementInit, index) in listInitExpression.Initializers.Select((e, i) => (e, i)))
                    {
                        handled = true;

                        if (index > 0)
                        {
                            Builder.Append(", ");
                        }

                        Visit(elementInit.Arguments[0]);
                    }

                    break;
                }

                case ConstantExpression constantExpression:
                {
                    var values = from object value in ((IEnumerable)constantExpression.Value)
                                 select Expression.Constant(value);

                    foreach (var (value, index) in values.Select((v, i) => (v, i)))
                    {
                        handled = true;

                        if (index > 0)
                        {
                            Builder.Append(", ");
                        }

                        Visit(value);
                    }

                    break;
                }

                case Expression expression:
                {
                    handled = true;

                    Builder.AddParameterList(expression, FormatParameterName);

                    break;
                }
            }

            if (!handled)
            {
                Builder.Append("SELECT 1 WHERE 1 = 0");
            }

            Builder.Append(")");

            return sqlInExpression;
        }

        protected virtual Expression VisitSqlParameterExpression(SqlParameterExpression sqlParameterExpression)
        {
            Builder.AddParameter(sqlParameterExpression.Expression, FormatParameterName);

            return sqlParameterExpression;
        }

        protected virtual Expression VisitSqlWindowFunctionExpression(SqlWindowFunctionExpression sqlWindowFunctionExpression)
        {
            Visit(sqlWindowFunctionExpression.Function);

            if (sqlWindowFunctionExpression.Ordering != null)
            {
                Builder.Append(" OVER(ORDER BY ");

                Visit(sqlWindowFunctionExpression.Ordering);

                Builder.Append(")");
            }

            return sqlWindowFunctionExpression;
        }

        protected virtual Expression VisitOrderByExpression(OrderByExpression orderByExpression)
        {
            var orderings = orderByExpression.Iterate().Reverse().ToArray();
            var hashes = new HashSet<int>();

            for (var i = 0; i < orderings.Length; i++)
            {
                if (i > 0)
                {
                    Builder.Append(", ");
                }

                var ordering = orderings[i];

                var detector = new SqlParameterDetectingExpressionVisitor();

                detector.Visit(ordering.Expression);

                var needsWrapping = detector.ParameterDetected;

                if (!needsWrapping)
                {
                    var hasher = new HashingExpressionVisitor();

                    hasher.Visit(ordering.Expression);

                    needsWrapping = !hashes.Add(hasher.HashCode) && !(ordering.Expression is RelationalQueryExpression);
                }

                if (needsWrapping)
                {
                    var wrapped
                        = new SingleValueRelationalQueryExpression(
                            new SelectExpression(
                                new ServerProjectionExpression(
                                    ordering.Expression)));

                    EmitExpressionListExpression(wrapped);
                }
                else
                {
                    EmitExpressionListExpression(ordering.Expression);
                }

                Builder.Append(" ");
                Builder.Append(ordering.Descending ? "DESC" : "ASC");
            }

            return orderByExpression;
        }

        #endregion

        #region Extensibility points

        protected virtual void EmitExpressionListExpression(Expression expression)
        {
            if (expression.Type.IsBooleanType()
                && !(expression is ConditionalExpression
                    || expression is ConstantExpression
                    || expression is SqlColumnExpression
                    || expression is SqlCastExpression
                    || expression is SqlParameterExpression
                    || expression is SingleValueRelationalQueryExpression))
            {
                Builder.Append("(CASE WHEN ");

                Visit(expression.AsSqlBooleanExpression());

                Builder.Append(" THEN 1 ELSE 0 END)");

                return;
            }

            if (expression is SqlAggregateExpression)
            {
                var type = expression.Type.UnwrapNullableType();

                if (type == typeof(float))
                {
                    Builder.Append("CAST(");

                    Visit(expression);

                    Builder.Append(" AS real)");

                    return;
                }
            }

            Visit(expression);
        }

        protected virtual string FormatIdentifier(string identifier)
        {
            return $"[{identifier}]";
        }

        protected virtual string FormatParameterName(string name)
        {
            return $"@{name}";
        }

        #endregion

        protected string GetTableAlias(AliasedTableExpression table)
        {
            if (!aliasLookup.TryGetValue(table, out var alias))
            {
                alias = table.Alias;

                if (!tableAliases.Add(alias))
                {
                    var i = 0;

                    do
                    {
                        alias = $"{table.Alias}_{i++}";
                    }
                    while (!tableAliases.Add(alias));
                }

                aliasLookup.Add(table, alias);
            }

            return alias;
        }

        protected static IEnumerable<(string alias, Expression expression)> FlattenProjection(Expression expression)
        {
            var visitor = new ProjectionLeafGatheringExpressionVisitor();

            visitor.Visit(expression);

            return visitor.GatheredExpressions.Select(p => (p.Key, p.Value)).ToArray();
        }

        protected static IEnumerable<(string alias, Expression expression)> FlattenProjection(ProjectionExpression projection)
        {
            IEnumerable<Expression> IterateServerProjectionExpressions(ProjectionExpression p)
            {
                switch (p)
                {
                    case ServerProjectionExpression server:
                    {
                        yield return server.ResultLambda.Body;
                        yield break;
                    }

                    case ClientProjectionExpression client:
                    {
                        yield return client.ServerProjection.ResultLambda.Body;
                        yield break;
                    }

                    case CompositeProjectionExpression composite:
                    {
                        foreach (var expression in IterateServerProjectionExpressions(composite.OuterProjection))
                        {
                            yield return expression;
                        }

                        foreach (var expression in IterateServerProjectionExpressions(composite.InnerProjection))
                        {
                            yield return expression;
                        }

                        yield break;
                    }
                }
            }

            var expressions = IterateServerProjectionExpressions(projection).ToArray();

            if (expressions.Length == 1)
            {
                var visitor = new ProjectionLeafGatheringExpressionVisitor();

                visitor.Visit(expressions[0]);

                foreach (var p in visitor.GatheredExpressions)
                {
                    yield return (p.Key, p.Value);
                }
            }
            else
            {
                for (var i = 0; i < expressions.Length; i++)
                {
                    var visitor = new ProjectionLeafGatheringExpressionVisitor();

                    visitor.Visit(expressions[i]);

                    foreach (var p in visitor.GatheredExpressions)
                    {
                        yield return ($"${i}." + p.Key, p.Value);
                    }
                }
            }
        }

        private class SqlParameterDetectingExpressionVisitor : ExpressionVisitor
        {
            public bool ParameterDetected { get; private set; }

            protected override Expression VisitExtension(Expression node)
            {
                switch (node)
                {
                    case SqlParameterExpression _ when !ParameterDetected:
                    {
                        ParameterDetected = true;

                        return node;
                    }

                    case RelationalQueryExpression _:
                    {
                        return node;
                    }

                    default:
                    {
                        return base.VisitExtension(node);
                    }
                }
            }
        }
    }
}