import unittest
from measure_design import normalized_signature


class SignatureTests(unittest.TestCase):
    def test_translation_rotation_and_reflection_are_not_layout_variety(self):
        source = [(0, 0, "A"), (1, 0, "A"), (2, 0, "B"), (0, 1, "C")]
        original = normalized_signature(source)
        self.assertEqual(original, normalized_signature((x+15, y-8, label) for x, y, label in source))
        self.assertEqual(original, normalized_signature((-y, x, label) for x, y, label in source))
        self.assertEqual(original, normalized_signature((-x, y, label) for x, y, label in source))

    def test_changed_shape_and_room_organization_are_distinguished(self):
        source = [(0, 0, "A"), (1, 0, "A"), (2, 0, "B"), (0, 1, "C")]
        self.assertNotEqual(normalized_signature(source), normalized_signature(source[:-1]+[(1, 1, "C")]))
        self.assertNotEqual(normalized_signature(source), normalized_signature([(x, y, "B" if label == "C" else label) for x, y, label in source]))
