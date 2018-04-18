﻿using Impatient.Extensions;
using Impatient.Query.ExpressionVisitors.Utility;
using System.Linq.Expressions;

namespace Impatient.Query.ExpressionVisitors.Optimizing
{
    public class BooleanOptimizingExpressionVisitor : ExpressionVisitor
    {
        private static readonly ExpressionVisitor binaryExpressionReducingExpressionVisitor
            = new BinaryExpressionReducingExpressionVisitor();

        private static readonly ExpressionVisitor unaryNotDistributingExpressionVisitor
            = new UnaryNotDistributingExpressionVisitor();

        public override Expression Visit(Expression node)
        {
            node = binaryExpressionReducingExpressionVisitor.Visit(node);
            node = unaryNotDistributingExpressionVisitor.Visit(node);

            return node;
        }

        // false && false -> false
        // true && false -> false
        // false && true -> false
        // true && true -> true
        // false && x -> false
        // true && x -> x
        // x && false -> false
        // x && true -> x
        // true || true -> true
        // true || false -> true
        // false || true -> true
        // false || false -> false
        // true || x -> true
        // false || x -> x
        // x || true -> true
        // x || false -> x
        // false == false -> true (not applied)
        // true == true -> true (not applied)
        // false == true -> false (not applied)
        // true == false -> false (not applied)
        // true == x -> x
        // false == x -> !x
        // x == true -> x
        // x == false -> !x
        // false != false -> false (not applied)
        // true != true -> false (not applied)
        // false != true -> true (not applied)
        // true != false -> true (not applied)
        // false != x -> x
        // true != x -> !x
        // x != false -> x
        // x != true -> !x
        private class BinaryExpressionReducingExpressionVisitor : ExpressionVisitor
        {
            protected override Expression VisitBinary(BinaryExpression node)
            {
                var left = Visit(node.Left).UnwrapInnerExpression();
                var right = Visit(node.Right).UnwrapInnerExpression();

                if (node.NodeType == ExpressionType.Equal || node.NodeType == ExpressionType.NotEqual)
                {
                    if (left is UnaryExpression leftUnaryExpression && left.NodeType == ExpressionType.Not)
                    {
                        if (right is UnaryExpression rightUnaryExpression && right.NodeType == ExpressionType.Not)
                        {
                            return node.Update(leftUnaryExpression.Operand, node.Conversion, rightUnaryExpression.Operand);
                        }
                    }
                }

                var leftConstant = left as ConstantExpression;
                var rightConstant = right as ConstantExpression;

                switch (node.NodeType)
                {
                    case ExpressionType.AndAlso:
                    {
                        if (leftConstant != null && rightConstant != null)
                        {
                            if (false.Equals(leftConstant.Value) || false.Equals(rightConstant.Value))
                            {
                                return Expression.Constant(false);
                            }
                            else if (true.Equals(leftConstant.Value) && true.Equals(rightConstant.Value))
                            {
                                return Expression.Constant(true);
                            }
                        }
                        else if (leftConstant != null)
                        {
                            if (false.Equals(leftConstant.Value))
                            {
                                return left;
                            }
                            else if (true.Equals(leftConstant.Value))
                            {
                                return right;
                            }
                        }
                        else if (rightConstant != null)
                        {
                            if (false.Equals(rightConstant.Value))
                            {
                                return right;
                            }
                            else if (true.Equals(rightConstant.Value))
                            {
                                return left;
                            }
                        }

                        break;
                    }

                    case ExpressionType.OrElse:
                    {
                        if (leftConstant != null && rightConstant != null)
                        {
                            if (true.Equals(leftConstant.Value) || true.Equals(rightConstant.Value))
                            {
                                return Expression.Constant(true);
                            }
                            else if (false.Equals(leftConstant.Value) && false.Equals(rightConstant.Value))
                            {
                                return Expression.Constant(false);
                            }
                        }
                        else if (leftConstant != null)
                        {
                            if (true.Equals(leftConstant.Value))
                            {
                                return left;
                            }
                            else if (false.Equals(leftConstant.Value))
                            {
                                return right;
                            }
                        }
                        else if (rightConstant != null)
                        {
                            if (true.Equals(rightConstant.Value))
                            {
                                return right;
                            }
                            else if (false.Equals(rightConstant.Value))
                            {
                                return left;
                            }
                        }

                        break;
                    }

                    case ExpressionType.Equal:
                    {
                        if (leftConstant != null
                            && rightConstant != null
                            && leftConstant.Type.IsBooleanType()
                            && rightConstant.Type.IsBooleanType())
                        {
                            if (leftConstant.Value.Equals(rightConstant.Value))
                            {
                                return Expression.Constant(true);
                            }
                            else
                            {
                                return Expression.Constant(false);
                            }
                        }
                        else if (leftConstant != null)
                        {
                            if (true.Equals(leftConstant.Value))
                            {
                                return right;
                            }
                            else if (false.Equals(leftConstant.Value))
                            {
                                return Expression.Not(right);
                            }
                        }
                        else if (rightConstant != null)
                        {
                            if (true.Equals(rightConstant.Value))
                            {
                                return left;
                            }
                            else if (false.Equals(rightConstant.Value))
                            {
                                return Expression.Not(left);
                            }
                        }

                        break;
                    }

                    case ExpressionType.NotEqual:
                    {
                        if (leftConstant != null
                            && rightConstant != null
                            && leftConstant.Type.IsBooleanType()
                            && rightConstant.Type.IsBooleanType())
                        {
                            if (leftConstant.Value.Equals(rightConstant.Value))
                            {
                                return Expression.Constant(false);
                            }
                            else
                            {
                                return Expression.Constant(true);
                            }
                        }
                        else if (leftConstant != null)
                        {
                            if (false.Equals(leftConstant.Value))
                            {
                                return right;
                            }
                            else if (true.Equals(leftConstant.Value))
                            {
                                return Expression.Not(right);
                            }
                        }
                        else if (rightConstant != null)
                        {
                            if (false.Equals(rightConstant.Value))
                            {
                                return left;
                            }
                            else if (true.Equals(rightConstant.Value))
                            {
                                return Expression.Not(left);
                            }
                        }

                        break;
                    }
                }

                if (left.Type != node.Left.Type)
                {
                    left = Expression.Convert(left, node.Left.Type);
                }

                if (right.Type != node.Right.Type)
                {
                    right = Expression.Convert(right, node.Right.Type);
                }

                return node.Update(left, node.Conversion, right);
            }
        }

        // !(true) -> false
        // !(false) -> true
        // !(!(x)) -> x
        // !(x == y) -> x != y
        // !(x != y) -> x == y
        // !(x && y) -> !x || !y
        // !(x || y) -> !x && !y
        // !(x > y) -> x <= y
        // !(x >= y) -> x < y
        // !(x < y) -> x >= y
        // !(x <= y) -> x > y
        private class UnaryNotDistributingExpressionVisitor : ExpressionVisitor
        {
            protected override Expression VisitUnary(UnaryExpression node)
            {
                var operand = Visit(node.Operand);

                if (node.NodeType == ExpressionType.Not)
                {
                    switch (operand.NodeType)
                    {
                        case ExpressionType.AndAlso:
                        case ExpressionType.OrElse:
                        case ExpressionType.Equal:
                        case ExpressionType.NotEqual:
                        case ExpressionType.GreaterThan:
                        case ExpressionType.GreaterThanOrEqual:
                        case ExpressionType.LessThan:
                        case ExpressionType.LessThanOrEqual:
                        {
                            // Immediately visiting the result ensures that any resulting double-nots are optimized.
                            return Visit(BinaryInvertingExpressionVisitor.Instance.Visit(operand));
                        }

                        case ExpressionType.Constant:
                        {
                            return true.Equals(((ConstantExpression)operand).Value)
                                ? Expression.Constant(false)
                                : Expression.Constant(true);
                        }

                        case ExpressionType.Not:
                        {
                            return ((UnaryExpression)operand).Operand;
                        }
                    }
                }

                return node.Update(operand);
            }
        }
    }
}
