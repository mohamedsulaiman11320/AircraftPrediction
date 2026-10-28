"""Scripted operational scenario: a rotating pair of at-risk aircraft (user decision 2026-10-28).

Configured in ``tools/simulator/telemetry_simulator.json`` -> ``scenario`` (data, not code). Every rotation period the
next two aircraft in ``expected_aircraft`` order are at risk: the first ``Critical`` (engine temperature above the
approved limit), the second ``HighCritical`` (temperature high AND oil pressure low); the rest are ``Healthy``. Values are
deterministic per aircraft and hour. Readings generated here are labelled by their telemetry_id prefix and are NOT
observations of real aircraft.
"""
from __future__ import annotations

import datetime as dt
import hashlib
import math
from dataclasses import dataclass
from typing import Any

import numpy as np

METRIC_KEYS = ("engine_temp_c", "oil_pressure_psi", "vibration_index")


@dataclass(frozen=True)
class ScenarioConfig:
    enabled: bool
    id_prefix: str
    anchor: dt.date
    rotation_days: int
    roles_per_period: tuple[str, ...]
    profiles: dict[str, dict[str, tuple[float, float]]]
    daily_amplitude: dict[str, float]
    rewrite_history_days: int

    @classmethod
    def from_dict(cls, c: dict[str, Any] | None) -> "ScenarioConfig | None":
        if not c:
            return None
        return cls(
            enabled=bool(c.get("enabled")), id_prefix=c["id_prefix"], anchor=dt.date.fromisoformat(c["anchor_date"]),
            rotation_days=int(c["rotation_days"]), roles_per_period=tuple(c["roles_per_period"]),
            profiles={role: {k: (float(v[0]), float(v[1])) for k, v in p.items()} for role, p in c["profiles"].items()},
            daily_amplitude={k: float(v) for k, v in c.get("daily_amplitude", {}).items()},
            rewrite_history_days=int(c.get("rewrite_history_days", 28)),
        )


def period_index(cfg: ScenarioConfig, when: dt.datetime) -> int:
    days = (when.astimezone(dt.timezone.utc).date() - cfg.anchor).days
    return math.floor(days / cfg.rotation_days)


def roles_at(cfg: ScenarioConfig, aircraft: tuple[str, ...], when: dt.datetime) -> dict[str, str]:
    """Role of every aircraft at ``when``: the period's at-risk pair rotates through the configured order."""
    n = len(aircraft)
    k = period_index(cfg, when)
    roles = {tail: "Healthy" for tail in aircraft}
    for i, role in enumerate(cfg.roles_per_period):
        roles[aircraft[(len(cfg.roles_per_period) * k + i) % n]] = role
    return roles


def _rng(tail: str, epoch: int) -> np.random.Generator:
    seed = int.from_bytes(hashlib.sha256(f"scenario:{tail}:{epoch}".encode()).digest()[:8], "big")
    return np.random.default_rng(seed)


def metrics_for(cfg: ScenarioConfig, tail: str, role: str, epoch: int, decimals: dict[str, int]) -> dict[str, float]:
    """Deterministic reading for an aircraft-hour: profile mean + daily cycle + normal noise."""
    rng = _rng(tail, epoch)
    hour = dt.datetime.fromtimestamp(epoch, dt.timezone.utc).hour
    profile = cfg.profiles[role]
    out = {}
    for key in METRIC_KEYS:
        mean, sd = profile[key]
        value = mean + cfg.daily_amplitude.get(key, 0.0) * math.sin(2 * math.pi * hour / 24) + rng.normal(0, sd)
        out[key] = round(max(value, 0.0), decimals.get(key, 2))
    return out
