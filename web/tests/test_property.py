"""Hypothesis property-based tests for the web app."""

import json
import string
from unittest.mock import patch

import pytest
import respx
from httpx import Response
from hypothesis import HealthCheck, given, settings
from hypothesis import strategies as st
from starlette.testclient import TestClient

# --- Custom strategies ---

# DNS label: 1-63 chars of [a-z0-9-], not starting/ending with hyphen
dns_label = st.text(
    alphabet=string.ascii_lowercase + string.digits + "-",
    min_size=1,
    max_size=63,
).filter(lambda s: not s.startswith("-") and not s.endswith("-") and "--" not in s[:2])

# Valid domain: 2-4 labels joined by dots
domain_name = st.builds(
    lambda labels: ".".join(labels),
    st.lists(dns_label, min_size=2, max_size=4),
)

# IPv4 address
ipv4 = st.builds(
    lambda octets: ".".join(str(o) for o in octets),
    st.lists(st.integers(min_value=0, max_value=255), min_size=4, max_size=4),
)

# Rewrite answer: either an IPv4 address or a domain (CNAME)
rewrite_answer = st.one_of(ipv4, domain_name)

# Profile name: printable non-empty string, no control chars
profile_name = (
    st.text(
        alphabet=string.ascii_letters + string.digits + " _-",
        min_size=1,
        max_size=50,
    )
    .map(str.strip)
    .filter(lambda s: len(s) > 0)
)

# Blocklist URL
blocklist_url = st.builds(
    lambda domain, path: f"https://{domain}/{path}.txt",
    domain_name,
    st.text(alphabet=string.ascii_lowercase, min_size=1, max_size=20),
)

# Client ID: IP, domain, or CIDR
client_id = st.one_of(
    ipv4,
    domain_name,
    st.builds(
        lambda ip, prefix: f"{ip}/{prefix}",
        ipv4,
        st.integers(min_value=8, max_value=32),
    ),
)

# Schedule window
schedule_window = st.fixed_dictionaries(
    {
        "allDay": st.booleans(),
        "start": st.sampled_from(["00:00", "06:00", "08:00", "12:00", "18:00", "20:00"]),
        "end": st.sampled_from(["06:00", "12:00", "17:00", "20:00", "23:59"]),
    }
)

DAYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"]

# Schedule: subset of days mapped to windows
schedule = st.one_of(
    st.none(),
    st.dictionaries(
        keys=st.sampled_from(DAYS),
        values=schedule_window,
        min_size=0,
        max_size=7,
    ),
)

# DNS rewrite entry
rewrite_entry = st.fixed_dictionaries(
    {
        "domain": domain_name,
        "answer": rewrite_answer,
    }
)

# Full profile config
profile_config = st.fixed_dictionaries(
    {
        "description": st.text(max_size=100),
        "blockedServices": st.lists(
            st.sampled_from(["youtube", "tiktok", "facebook", "instagram", "twitter"]),
            max_size=5,
            unique=True,
        ),
        "blockLists": st.lists(blocklist_url, max_size=3, unique=True),
        "allowList": st.lists(domain_name, max_size=5),
        "customRules": st.lists(domain_name, max_size=5),
        "dnsRewrites": st.lists(rewrite_entry, max_size=3),
        "schedule": schedule,
    }
)

# Client config
client_config = st.fixed_dictionaries(
    {
        "name": st.text(
            alphabet=string.ascii_letters + string.digits + " -",
            min_size=1,
            max_size=30,
        )
        .map(str.strip)
        .filter(lambda s: len(s) > 0),
        "ids": st.lists(client_id, min_size=1, max_size=3),
        "profile": st.text(alphabet=string.ascii_letters + string.digits, min_size=0, max_size=20),
    }
)

# Full app config
app_config = st.fixed_dictionaries(
    {
        "enableBlocking": st.booleans(),
        "profiles": st.dictionaries(profile_name, profile_config, min_size=0, max_size=5),
        "clients": st.lists(client_config, max_size=5),
        "defaultProfile": st.one_of(st.none(), profile_name),
        "baseProfile": st.one_of(st.none(), profile_name),
        "timeZone": st.sampled_from(
            [
                "UTC",
                "America/Denver",
                "America/New_York",
                "Asia/Tokyo",
                "Europe/London",
            ]
        ),
        "scheduleAllDay": st.booleans(),
        "customServices": st.dictionaries(
            st.text(
                alphabet=string.ascii_lowercase + "-",
                min_size=1,
                max_size=20,
            ).filter(lambda s: not s.startswith("-")),
            st.fixed_dictionaries(
                {
                    "name": st.text(min_size=1, max_size=30),
                    "domains": st.lists(domain_name, max_size=3),
                }
            ),
            max_size=3,
        ),
        "blockLists": st.lists(
            st.fixed_dictionaries(
                {
                    "url": blocklist_url,
                    "name": st.text(max_size=30),
                    "enabled": st.booleans(),
                    "refreshHours": st.integers(min_value=1, max_value=168),
                }
            ),
            max_size=3,
        ),
        "_blockListsSeeded": st.just(True),
    }
)


def _make_client(tmp_path, config_data):
    """Helper to create a patched TestClient with given config."""
    config_path = tmp_path / "dnsApp.config"
    config_path.write_text(json.dumps(config_data, indent=2))
    services_path = tmp_path / "blocked-services.json"
    services_path.write_text(json.dumps({}))

    with (
        patch("technitium_content_filter.config.CONFIG_PATH", config_path),
        patch("technitium_content_filter.config.BLOCKED_SERVICES_PATH", services_path),
        patch("technitium_content_filter.config.TECHNITIUM_API_TOKEN", "test-token"),
        patch("technitium_content_filter.config.TECHNITIUM_URL", "http://technitium-mock:5380"),
        patch("technitium_content_filter.config.AUTH_DISABLED", True),
        respx.mock(assert_all_called=False) as mock,
    ):
        mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
            return_value=Response(200, json={"status": "ok"})
        )
        from technitium_content_filter.app import app

        yield TestClient(app, raise_server_exceptions=True), config_path


# --- Property tests ---


@pytest.mark.property
class TestConfigRoundTrip:
    @given(config=app_config)
    @settings(max_examples=50, suppress_health_check=[HealthCheck.function_scoped_fixture])
    def test_save_load_identity(self, config, tmp_path):
        """Any valid config survives save -> load without data loss."""
        config_path = tmp_path / "dnsApp.config"

        with patch("technitium_content_filter.config.CONFIG_PATH", config_path):
            from technitium_content_filter.config import load_config, save_config

            save_config(config)
            loaded = load_config()

        # All original keys should be present
        for key in config:
            assert key in loaded, f"Key {key!r} missing after round-trip"

        # Core fields should be identical
        assert loaded["enableBlocking"] == config["enableBlocking"]
        assert loaded["timeZone"] == config["timeZone"]
        assert loaded["scheduleAllDay"] == config["scheduleAllDay"]
        assert loaded["defaultProfile"] == config["defaultProfile"]
        assert loaded["baseProfile"] == config["baseProfile"]
        assert loaded["clients"] == config["clients"]
        assert loaded["customServices"] == config["customServices"]

        # Profiles should match
        assert set(loaded["profiles"].keys()) == set(config["profiles"].keys())

    @given(config=app_config)
    @settings(max_examples=30, suppress_health_check=[HealthCheck.function_scoped_fixture])
    def test_save_produces_valid_json(self, config, tmp_path):
        """Saved config is always valid JSON."""
        config_path = tmp_path / "dnsApp.config"

        with patch("technitium_content_filter.config.CONFIG_PATH", config_path):
            from technitium_content_filter.config import save_config

            save_config(config)

        text = config_path.read_text()
        parsed = json.loads(text)  # Should not raise
        assert isinstance(parsed, dict)


@pytest.mark.property
class TestMigrationIdempotency:
    @given(config=app_config)
    @settings(max_examples=50, suppress_health_check=[HealthCheck.function_scoped_fixture])
    def test_migrate_idempotent(self, config):
        """Running migration twice produces identical config."""
        from technitium_content_filter.config import _migrate_blocklists

        _migrate_blocklists(config)
        first = json.dumps(config, sort_keys=True)

        _migrate_blocklists(config)
        second = json.dumps(config, sort_keys=True)

        assert first == second

    @given(
        profiles=st.dictionaries(
            profile_name,
            st.lists(
                st.one_of(
                    blocklist_url,
                    st.fixed_dictionaries(
                        {
                            "url": blocklist_url,
                            "name": st.text(max_size=20),
                            "enabled": st.booleans(),
                            "refreshHours": st.integers(min_value=1, max_value=168),
                        }
                    ),
                ),
                max_size=3,
            ),
            max_size=3,
        ),
    )
    @settings(max_examples=50, suppress_health_check=[HealthCheck.function_scoped_fixture])
    def test_migrate_always_produces_string_urls(self, profiles):
        """After migration, all profile blockLists entries are strings."""
        from technitium_content_filter.config import _migrate_blocklists

        config = {
            "profiles": {name: {"blockLists": bls} for name, bls in profiles.items()},
            "blockLists": [],
        }
        _migrate_blocklists(config)

        for name, profile in config["profiles"].items():
            for entry in profile["blockLists"]:
                assert isinstance(entry, str), f"Profile {name!r} has non-string entry: {entry!r}"


@pytest.mark.property
class TestApiNeverCrashes:
    @given(name=profile_name, config=profile_config)
    @settings(max_examples=30, suppress_health_check=[HealthCheck.function_scoped_fixture])
    def test_profile_save_never_500s(self, name, config, tmp_path):
        """Saving any valid profile never causes a server error."""
        for c, _ in _make_client(
            tmp_path,
            {
                "enableBlocking": True,
                "profiles": {},
                "clients": [],
                "defaultProfile": None,
                "baseProfile": None,
                "timeZone": "UTC",
                "scheduleAllDay": True,
                "customServices": {},
                "blockLists": [],
                "_blockListsSeeded": True,
            },
        ):
            resp = c.post("/api/profiles", json={"name": name, **config})
            assert resp.status_code < 500, f"Server error for profile {name!r}"

    @given(domain=domain_name, answer=rewrite_answer)
    @settings(max_examples=30, suppress_health_check=[HealthCheck.function_scoped_fixture])
    def test_rewrite_save_never_500s(self, domain, answer, tmp_path):
        """Saving any valid rewrite never causes a server error."""
        base_config = {
            "enableBlocking": True,
            "profiles": {"test": {"dnsRewrites": []}},
            "clients": [],
            "defaultProfile": None,
            "baseProfile": None,
            "timeZone": "UTC",
            "scheduleAllDay": True,
            "customServices": {},
            "blockLists": [],
            "_blockListsSeeded": True,
        }
        for c, _ in _make_client(tmp_path, base_config):
            resp = c.post(
                "/api/profiles/test/rewrites",
                json={
                    "domain": domain,
                    "answer": answer,
                },
            )
            assert resp.status_code < 500

    @given(ids=st.lists(client_id, min_size=1, max_size=5))
    @settings(max_examples=30, suppress_health_check=[HealthCheck.function_scoped_fixture])
    def test_client_save_never_500s(self, ids, tmp_path):
        """Saving client with any valid IDs never causes a server error."""
        base_config = {
            "enableBlocking": True,
            "profiles": {},
            "clients": [],
            "defaultProfile": None,
            "baseProfile": None,
            "timeZone": "UTC",
            "scheduleAllDay": True,
            "customServices": {},
            "blockLists": [],
            "_blockListsSeeded": True,
        }
        for c, _ in _make_client(tmp_path, base_config):
            resp = c.post(
                "/api/clients",
                json={
                    "name": "Test",
                    "ids": ids,
                    "profile": "",
                },
            )
            assert resp.status_code < 500


@pytest.mark.property
class TestRewriteNormalization:
    @given(domain=domain_name)
    @settings(
        max_examples=50, deadline=500, suppress_health_check=[HealthCheck.function_scoped_fixture]
    )
    def test_domain_lowercased_and_dot_stripped(self, domain, tmp_path):
        """Domain is always lowercased and trailing dot stripped."""
        variants = [domain, domain.upper(), domain + ".", " " + domain + " "]
        base_config = {
            "enableBlocking": True,
            "profiles": {"test": {"dnsRewrites": []}},
            "clients": [],
            "defaultProfile": None,
            "baseProfile": None,
            "timeZone": "UTC",
            "scheduleAllDay": True,
            "customServices": {},
            "blockLists": [],
            "_blockListsSeeded": True,
        }

        for variant in variants:
            for c, config_path in _make_client(tmp_path, base_config):
                resp = c.post(
                    "/api/profiles/test/rewrites",
                    json={
                        "domain": variant,
                        "answer": "1.2.3.4",
                    },
                )
                if resp.status_code == 200:
                    saved = json.loads(config_path.read_text())
                    rewrites = saved["profiles"]["test"]["dnsRewrites"]
                    assert len(rewrites) > 0
                    saved_domain = rewrites[-1]["domain"]
                    assert saved_domain == saved_domain.lower()
                    assert not saved_domain.endswith(".")


@pytest.mark.property
class TestProfileDeleteCascade:
    @given(
        profile_names=st.lists(profile_name, min_size=1, max_size=5, unique=True),
        delete_idx=st.integers(min_value=0),
    )
    @settings(max_examples=30, suppress_health_check=[HealthCheck.function_scoped_fixture])
    def test_delete_removes_profile_and_unassigns(self, profile_names, delete_idx, tmp_path):
        """Deleting a profile always unassigns all its clients."""
        delete_idx = delete_idx % len(profile_names)
        target = profile_names[delete_idx]

        config = {
            "enableBlocking": True,
            "profiles": {name: {"blockedServices": []} for name in profile_names},
            "clients": [
                {"name": f"device-{i}", "ids": [f"10.0.0.{i}"], "profile": name}
                for i, name in enumerate(profile_names)
            ],
            "defaultProfile": None,
            "baseProfile": None,
            "timeZone": "UTC",
            "scheduleAllDay": True,
            "customServices": {},
            "blockLists": [],
            "_blockListsSeeded": True,
        }

        for c, config_path in _make_client(tmp_path, config):
            c.request("DELETE", "/api/profiles", json={"name": target})

            saved = json.loads(config_path.read_text())
            assert target not in saved["profiles"]
            for client_entry in saved["clients"]:
                if client_entry["profile"] == target:
                    msg = (
                        f"Client {client_entry['name']} still assigned to deleted profile {target}"
                    )
                    raise AssertionError(msg)
