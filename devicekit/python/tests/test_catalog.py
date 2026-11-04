import unittest

from surface_devicekit.catalog import BudgetOverhead, BudgetRule, Catalog, ModelSizes, ModelSpec, Thresholds

from tests.support import catalog, catalog_with


class MatchTests(unittest.TestCase):
    def test_the_first_silicon_entry_in_catalog_order_that_matches_wins(self):
        cases = {
            "NVIDIA RTX Spark N1X (6144-core GPU, 20-core CPU)": "nvidia-n1x",
            "Snapdragon(R) X2 Elite Extreme - X2E-96-100": "snapdragon-x2",
            "Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Oryon(TM) CPU": "snapdragon-x",
            "Snapdragon X Plus - X1P64100": "snapdragon-x",
            "Microsoft SQ3": "snapdragon-legacy",
            "Intel(R) Core(TM) Ultra 7 350": "intel-core-ultra-3",
            "Intel(R) Core(TM) Ultra 7 258V": "intel-core-ultra-lunar",
            "Intel(R) Core(TM) Ultra 7 265H": "intel-core-ultra-arrow",
            "Intel(R) Core(TM) Ultra 7 155H": "intel-core-ultra-meteor",
            "Intel(R) Core(TM) i7-1255U": "intel-core",
            "Intel(R) Pentium(R) Silver N6000": "intel-other",
            "AMD Ryzen AI 9 HX 370 w/ Radeon 890M": "amd-ryzen-ai",
            "AMD Ryzen 7 7840U w/ Radeon 780M Graphics": "amd-ryzen",
            "Contoso Octa 9000": None,
        }
        for cpu, expected in cases.items():
            with self.subTest(cpu):
                found = catalog().match_silicon(cpu)
                self.assertEqual(found.id if found else None, expected)

    def test_the_first_gpu_family_in_catalog_order_that_matches_wins(self):
        cases = {
            "NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)": "nvidia-n1x",  # says RTX, but the N1X family comes first
            "NVIDIA GeForce RTX 4060 Laptop GPU": "nvidia-rtx",
            "NVIDIA GeForce GTX 1650": "nvidia-gtx",
            "NVIDIA TITAN GTX": "nvidia-gtx",
            "Intel(R) Arc(TM) Graphics": "intel-arc",
            "Intel(R) UHD Graphics": "intel-arc",
            "AMD Radeon(TM) Graphics": "amd-radeon",
            "Qualcomm(R) Adreno(TM) X1-85 GPU": "adreno",
            "NVIDIA Tesla T4": None,
        }
        for name, expected in cases.items():
            with self.subTest(name):
                found = catalog().match_gpu(name)
                self.assertEqual(found.id if found else None, expected)

    def test_matching_ignores_case(self):
        self.assertEqual(catalog().match_silicon("snapdragon(r) x elite - x1e80100").id, "snapdragon-x")
        self.assertEqual(catalog().match_gpu("nvidia geforce rtx 4060").id, "nvidia-rtx")


class RamTierTests(unittest.TestCase):
    def test_a_tier_is_the_largest_standard_size_within_eight_percent_of_the_memory(self):
        cases = [
            (0, 0), (4, 0), (7.4, 0), (7.41, 8), (7.9, 8), (8, 8), (14.81, 8), (14.82, 16), (15.6, 16), (16, 16), (29.6, 24), (29.63, 32),
            (31.99, 32), (63.98, 64), (127.9, 128), (200, 128),
        ]
        for gb, tier in cases:
            with self.subTest(gb):
                self.assertEqual(catalog().ram_tier(gb), tier)

    def test_the_tiers_and_the_tolerance_come_from_the_catalog_whatever_order_the_tiers_are_in(self):
        cat = catalog_with(lambda d: d.update(tierTolerance=0.0, ramTiers=[32, 8, 16]))
        self.assertEqual([cat.ram_tier(gb) for gb in (7.99, 8.0, 15.99, 16.0, 31.99, 32.0, 100.0)], [0, 8, 8, 16, 16, 32, 32])


class LoadCostTests(unittest.TestCase):
    def test_the_cost_is_the_file_size_times_the_multiplier_plus_the_constant(self):
        for size, cost in ((0.0, 0.5), (1.0, 1.65), (5.0, 6.25), (10.0, 12.0)):
            with self.subTest(size):
                self.assertAlmostEqual(catalog().load_cost(size), cost)

    def test_the_multiplier_and_the_constant_come_from_the_catalog(self):
        cat = catalog_with(lambda d: d["budget"].update(overhead={"multiplier": 2.0, "constantGb": 1.0}))
        self.assertEqual(cat.load_cost(3.0), 7.0)


class ModelSizeTests(unittest.TestCase):
    def test_a_lane_prefers_the_file_made_for_it_and_falls_back_to_the_other_one(self):
        both = ModelSpec(size_mb=ModelSizes(gpu=2048.0, cpu=1024.0))
        gpu_only = ModelSpec(size_mb=ModelSizes(gpu=2048.0))
        cpu_only = ModelSpec(size_mb=ModelSizes(cpu=1024.0))
        self.assertEqual((both.size_for("gpu"), both.size_for("cpu")), ((2.0, False), (1.0, False)))
        self.assertEqual(gpu_only.size_for("cpu"), (2.0, False))
        self.assertEqual(cpu_only.size_for("gpu"), (1.0, False))
        self.assertIsNone(ModelSpec().size_for("gpu"))

    def test_an_npu_uses_the_gpu_size_as_an_estimate_and_only_an_npu_does(self):
        self.assertEqual(ModelSpec(size_mb=ModelSizes(gpu=2048.0, cpu=1024.0)).size_for("npu"), (2.0, True))
        self.assertEqual(ModelSpec(size_mb=ModelSizes(cpu=1024.0)).size_for("npu"), (1.0, True))


class ClassRankTests(unittest.TestCase):
    def test_classes_rank_in_catalog_order_and_an_unknown_class_ranks_with_the_smallest(self):
        classes = catalog().model_classes
        self.assertEqual([classes.rank(c) for c in ("XS", "S", "M", "L", "XL")], [0, 1, 2, 3, 4])
        self.assertEqual(classes.rank("unheard-of"), 0)


class ParseTests(unittest.TestCase):
    def test_an_empty_document_and_a_document_that_is_not_an_object_are_refused(self):
        with self.assertRaisesRegex(ValueError, "^The device catalog is empty\\.$"):
            Catalog.parse("null")
        with self.assertRaisesRegex(ValueError, "^\\$: expected a JSON object\\.$"):
            Catalog.parse("[]")
        with self.assertRaises(ValueError):
            Catalog.parse("")

    def test_a_missing_section_keeps_its_defaults(self):
        cat = Catalog.parse("{}")
        self.assertEqual((cat.tier_tolerance, cat.thresholds, cat.budget.overhead, cat.surface.product_pattern), (0.08, Thresholds(), BudgetOverhead(), "^Surface"))
        self.assertEqual((cat.ram_tiers, cat.silicon, cat.models, cat.workloads), ((), (), {}, {}))

    def test_names_match_without_regard_to_case_and_comments_are_allowed(self):
        cat = Catalog.parse('{ // a note\n "CatalogVersion": "v9", "RAMTIERS": [8], "Thresholds": {"GPUMINBUDGETGB": 3} }')
        self.assertEqual((cat.catalog_version, cat.ram_tiers, cat.thresholds.gpu_min_budget_gb), ("v9", (8,), 3.0))

    def test_a_budget_rule_is_any_entry_of_the_budget_section_other_than_the_note_and_the_overhead(self):
        cat = Catalog.parse('{"budget": {"note": "n", "overhead": {"multiplier": 2}, "custom": {"basis": "vram", "fraction": 0.5, "reserveGb": 1}}}')
        self.assertEqual(cat.budget.rule("custom"), BudgetRule("vram", 0.5, 1.0))
        self.assertEqual(cat.budget.rule("unlisted"), BudgetRule("ram", 1.0, 0.0))
        self.assertEqual(cat.budget.overhead, BudgetOverhead(2.0, 0.5))

    def test_a_pattern_that_does_not_compile_is_named_with_its_place(self):
        with self.assertRaisesRegex(ValueError, "^\\$\\.silicon\\[0\\]\\.cpuMatch: invalid regular expression \\("):
            Catalog.parse('{"silicon": [{"id": "x", "cpuMatch": "(unclosed"}]}')
        with self.assertRaisesRegex(ValueError, "^\\$\\.gpuFamilies\\[0\\]\\.match: invalid regular expression \\("):
            Catalog.parse('{"gpuFamilies": [{"id": "x", "match": "[a"}]}')

    def test_a_value_of_the_wrong_type_is_named_with_its_place(self):
        cases = {
            '{"thresholds": {"gpuMinBudgetGb": "two"}}': "$.thresholds.gpuMinBudgetGb: expected a number.",
            '{"models": {"m": {"sizeMb": {"gpu": true}}}}': "$.models.m.sizeMb.gpu: expected a number.",
            '{"ramTiers": [8, "16"]}': "$.ramTiers: expected an array of integers.",
            '{"silicon": {}}': "$.silicon: expected an array.",
            '{"workloads": {"w": {"candidates": "a"}}}': "$.workloads.w.candidates: expected an array of strings.",
        }
        for text, message in cases.items():
            with self.subTest(text), self.assertRaises(ValueError) as refused:
                Catalog.parse(text)
            self.assertEqual(str(refused.exception), message)
