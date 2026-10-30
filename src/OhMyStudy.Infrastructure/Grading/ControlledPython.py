import ast
import json
import math
import operator
import re

def calculate(source):
    import sympy
    functions = {name: getattr(math, name) for name in (
        'sqrt', 'sin', 'cos', 'tan', 'asin', 'acos', 'atan', 'atan2', 'log', 'log10',
        'exp', 'floor', 'ceil', 'factorial', 'comb', 'perm', 'gcd', 'lcm', 'degrees', 'radians')}
    functions.update({name: getattr(sympy, name) for name in (
        'symbols', 'Symbol', 'Rational', 'Integer', 'Matrix', 'Eq', 'solve', 'linsolve',
        'diff', 'integrate', 'limit', 'simplify', 'expand', 'factor', 'sin', 'cos', 'tan',
        'sqrt', 'log', 'exp', 'Abs', 'N', 'det', 'transpose')})
    functions.update({'abs': abs, 'round': round, 'sum': sum, 'min': min, 'max': max, 'len': len})
    values = {'pi': sympy.pi, 'E': sympy.E, 'oo': sympy.oo}
    binary = {ast.Add: operator.add, ast.Sub: operator.sub, ast.Mult: operator.mul,
              ast.Div: operator.truediv, ast.FloorDiv: operator.floordiv, ast.Mod: operator.mod, ast.Pow: operator.pow}
    tree = ast.parse(source)
    if len(list(ast.walk(tree))) > 2000:
        raise ValueError('The calculation exceeds 2000 syntax nodes.')
    def contains_text(item):
        return isinstance(item, str) or isinstance(item, (list, tuple)) and any(contains_text(part) for part in item)
    def value(node):
        if isinstance(node, ast.Constant) and isinstance(node.value, (str, int, float, bool)):
            return node.value
        if isinstance(node, ast.Name) and node.id in values:
            return values[node.id]
        if isinstance(node, (ast.List, ast.Tuple)):
            return [value(item) for item in node.elts]
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, (ast.UAdd, ast.USub)):
            result = value(node.operand)
            return -result if isinstance(node.op, ast.USub) else result
        if isinstance(node, ast.BinOp) and type(node.op) in binary:
            left, right = value(node.left), value(node.right)
            if isinstance(left, (str, list, tuple)) or isinstance(right, (str, list, tuple)):
                raise ValueError('Sequence arithmetic is not allowed. Use Matrix for matrix calculations.')
            if isinstance(node.op, ast.Pow) and isinstance(right, (int, float)) and abs(right) > 10000:
                raise ValueError('Exponent exceeds the calculation limit.')
            return binary[type(node.op)](left, right)
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id in functions and not node.keywords:
            args = [value(arg) for arg in node.args]
            if node.func.id in ('symbols', 'Symbol'):
                if len(args) != 1 or not isinstance(args[0], str) or len(args[0]) > 256 or not re.fullmatch(r'[A-Za-z][A-Za-z0-9_, ]*', args[0]):
                    raise ValueError('Use simple Latin symbol names, separated by spaces or commas.')
            elif any(contains_text(arg) for arg in args):
                raise ValueError('Strings cannot be passed to mathematical functions. Create symbols first and pass mathematical expressions.')
            return functions[node.func.id](*args)
        if isinstance(node, ast.Subscript):
            container, index = value(node.value), value(node.slice)
            if isinstance(index, int) and isinstance(container, (list, tuple, sympy.MatrixBase, sympy.Tuple)):
                return container[index]
        raise ValueError('Only mathematical expressions and approved functions are allowed. No imports, attributes, files, network, or shell.')
    for statement in tree.body:
        if isinstance(statement, ast.Assign) and len(statement.targets) == 1 and isinstance(statement.targets[0], ast.Name):
            name = statement.targets[0].id
            if name.startswith('_') or name in functions or name == 'print':
                raise ValueError('Invalid calculation variable name.')
            values[name] = value(statement.value)
        elif isinstance(statement, ast.Expr):
            expression = statement.value
            if isinstance(expression, ast.Call) and isinstance(expression.func, ast.Name) and expression.func.id == 'print' and not expression.keywords:
                print(*(value(arg) for arg in expression.args))
            else:
                print(value(expression))
        else:
            raise ValueError('Only mathematical expressions and simple assignments are allowed.')

calculate(json.loads(SOURCE_JSON))
