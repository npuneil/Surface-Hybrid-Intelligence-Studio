"""python -m surface_devicekit: detect this PC, or pretend to be another, and print what it can do. Same options as the C# surface-device tool."""

from __future__ import annotations

import re
import sys
from collections.abc import Sequence

from . import golden, selftest, simulate
from .catalog import Catalog
from .dotnet import fmt
from .jsonio import parse_raw, read_text, to_json
from .models import SimulatedDevice, UnknownKeyError, WorkloadPlan
from .probe import PlatformNotSupportedError
from .report import render
from .service import DeviceDetectionService, analyze

USAGE = "\n".join(
    (
        "python -m surface_devicekit [options]",
        "",
        "  (no options)           Report for this PC",
        "  --json                 Print JSON instead of text",
        "  --raw                  Print the raw hardware the probe collected (JSON)",
        "  --raw-file FILE        Classify raw hardware captured on another PC (output of --raw)",
        "  --list                 List the simulated devices",
        "  --simulate ID          Report for a simulated device",
        "  --plan WORKLOAD        Only the plan for one workload",
        "  --all-simulated        With --json: every simulated device in one object",
        "  --golden               Print the golden vectors (the structural answers both ports are tested against)",
        "  --check-golden FILE    Compare this build's answers with a golden-vectors file; exit 1 on any difference",
        "  --self-test            Check the catalog and the known answers",
        "  --catalog FILE         Use a different device-catalog.json",
    )
)


def _flag(args: list[str], name: str) -> bool:
    if name not in args:
        return False
    args.remove(name)
    return True


def _value(args: list[str], name: str) -> str | None:
    if name not in args:
        return None
    i = args.index(name)
    if i + 1 >= len(args):
        raise ValueError(f"{name} needs a value.")
    value = args[i + 1]
    del args[i : i + 2]
    return value


def _plan_text(plan: WorkloadPlan) -> str:
    lanes = "\n".join(
        f"  {lane.lane:<3} budget {fmt(lane.budget_gb, 1):>5} GB  ladder: {' > '.join(m.alias for m in lane.ladder) if lane.ladder else lane.reason}"
        for lane in plan.lanes
    )
    return f"{plan.title}\n  {plan.summary}\n{lanes}"


def _list_text() -> str:
    groups: dict[str, list[SimulatedDevice]] = {}
    for device in simulate.all_devices():
        groups.setdefault(device.group, []).append(device)
    return "\n".join(f"{group}\n" + "\n".join(f"  {d.id:<34} {d.label}" for d in devices) for group, devices in groups.items())


def _run(args: list[str]) -> int:
    # Each option is removed as it is read, so the order below decides which one claims an ambiguous token.
    if _flag(args, "--help") or _flag(args, "-h") or _flag(args, "-?"):
        print(USAGE)
        return 0
    as_json = _flag(args, "--json")
    show_raw = _flag(args, "--raw")
    list_devices = _flag(args, "--list")
    all_simulated = _flag(args, "--all-simulated")
    print_golden = _flag(args, "--golden")
    check_golden = _value(args, "--check-golden")
    self_test = _flag(args, "--self-test")
    raw_file = _value(args, "--raw-file")
    simulate_id = _value(args, "--simulate")
    workload = _value(args, "--plan")
    catalog_file = _value(args, "--catalog")
    if args:
        raise ValueError(f"Unknown option '{args[0]}'.\n\n{USAGE}")

    catalog = Catalog.default() if catalog_file is None else Catalog.parse(read_text(catalog_file))

    if self_test:
        results = selftest.run(catalog)
        for result in results:
            detail = f"  <- {result.detail}" if not result.passed and result.detail else ""
            print(f"{'PASS' if result.passed else 'FAIL'}  {result.name}{detail}")
        failed = sum(1 for result in results if not result.passed)
        print(f"\nDevice kit self-test: {len(results) - failed}/{len(results)} checks passed")
        return 0 if failed == 0 else 1

    if print_golden:
        print(golden.build(catalog))
        return 0

    if check_golden is not None:
        diffs = golden.compare(read_text(check_golden), golden.build(catalog))
        for diff in diffs[:40]:
            print(diff)
        if diffs:
            print(f"{len(diffs)} difference(s) from {check_golden}. Regenerate with --golden if the change was intended.")
            return 1
        print("Golden vectors match.")
        return 0

    if list_devices:
        print(_list_text())
        return 0

    if all_simulated:
        print(to_json({d.id: analyze(d.raw, catalog, "simulated", d.id, d.label) for d in simulate.all_devices()}))
        return 0

    if raw_file is not None:
        report = analyze(parse_raw(read_text(raw_file)), catalog, "detected")
    elif simulate_id is not None:
        sim = simulate.find(simulate_id)
        if sim is None:
            raise ValueError(f"No simulated device '{simulate_id}'. Try --list.")
        report = analyze(sim.raw, catalog, "simulated", sim.id, sim.label)
    else:
        service = DeviceDetectionService(catalog)
        if show_raw:
            print(to_json(service.detected.profile.raw))
            return 0
        report = service.detected

    if workload is not None:
        plan = report.plans.get(workload)
        if plan is None:
            raise UnknownKeyError(f"Unknown workload '{workload}'. Known: {', '.join(report.plans)}")
        print(to_json(plan) if as_json else _plan_text(plan))
        return 0

    print(to_json(report) if as_json else render(report, catalog))
    return 0


def _message(error: Exception) -> str:
    if isinstance(error, KeyError) and not isinstance(error, UnknownKeyError):
        return f"The given key '{error.args[0]}' was not present in the dictionary."
    return str(error)


def main(argv: Sequence[str] | None = None) -> int:
    try:
        return _run(list(sys.argv[1:] if argv is None else argv))
    except (OSError, ValueError, KeyError, re.error, PlatformNotSupportedError) as error:
        print(_message(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    # A console whose code page cannot show "·" or "—" should print "?" like the C# tool does, not stop with an encoding error.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    sys.exit(main())
