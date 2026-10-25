import ast
from pathlib import Path
import tempfile
import unittest


class DocResPipelineChecks(unittest.TestCase):
    def test_only_selected_official_tasks_receive_the_unmodified_input(self):
        source = Path(__file__).resolve().parents[1] / "docres/worker.py"
        tree = ast.parse(source.read_text(encoding="utf-8"))
        function = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "restore_official")
        order = ["dewarping", "deshadowing", "appearance", "deblurring"]
        for combination in range(16):
            calls = []
            saved = []
            functions = {}
            for task in order:
                def run(model, path, task=task):
                    calls.append((task, model, path))
                    return None, None, None, task
                functions[task] = run
            namespace = {"Path": Path, "cv2": type("ImageWriter", (), {"imwrite": staticmethod(lambda path, image: saved.append((path, image)) or True)})}
            exec(compile(ast.Module(body=[function], type_ignores=[]), str(source), "exec"), namespace)
            with tempfile.TemporaryDirectory() as directory:
                original = Path(directory) / "original.jpg"
                original.write_bytes(b"unchanged original bytes")
                tasks = [task for index, task in enumerate(order) if combination & (1 << index)]
                model = object()
                result = namespace["restore_official"](original, Path(directory) / "result.png", tasks, model, functions, lambda *args, **kwargs: None)
                self.assertEqual([call[0] for call in calls], tasks)
                self.assertEqual(original.read_bytes(), b"unchanged original bytes")
                if calls:
                    self.assertEqual(calls[0][2], str(original))
                    self.assertIs(calls[0][1], model)
                    self.assertEqual(result, tasks[-1])
                    self.assertEqual(len(saved), len(tasks))
                    for previous, following in zip(saved, calls[1:]):
                        self.assertEqual(following[2], previous[0])
                else:
                    self.assertIsNone(result)
                    self.assertEqual(saved, [])


if __name__ == "__main__":
    unittest.main()
