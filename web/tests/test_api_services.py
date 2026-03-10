"""API tests for services endpoints."""

import pytest

from tests.conftest import read_config


@pytest.mark.api
class TestServicesGet:
    def test_returns_merged_services(self, client):
        resp = client.get("/api/services")
        assert resp.status_code == 200
        services = resp.json()
        # Built-in from blocked-services.json
        assert "youtube" in services
        assert "tiktok" in services
        # Custom from config
        assert "my-streaming" in services

    def test_custom_overrides_builtin(self, client, tmp_config):
        """If custom service has same ID as built-in, custom wins."""
        config = read_config(tmp_config)
        config["customServices"]["youtube"] = {
            "name": "Custom YouTube",
            "domains": ["custom-yt.com"],
        }
        tmp_config.write_text(__import__("json").dumps(config))

        resp = client.get("/api/services")
        services = resp.json()
        assert services["youtube"]["name"] == "Custom YouTube"


@pytest.mark.api
class TestCustomServiceSave:
    def test_create_custom_service(self, client, tmp_config):
        resp = client.post(
            "/api/custom-services",
            json={
                "id": "gaming",
                "name": "Gaming Platforms",
                "domains": ["steam.com", "epic.com"],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "gaming" in config["customServices"]
        assert config["customServices"]["gaming"]["name"] == "Gaming Platforms"
        assert len(config["customServices"]["gaming"]["domains"]) == 2

    def test_update_custom_service(self, client, tmp_config):
        client.post(
            "/api/custom-services",
            json={
                "id": "my-streaming",
                "name": "Updated Streaming",
                "domains": ["new.com"],
            },
        )
        config = read_config(tmp_config)
        assert config["customServices"]["my-streaming"]["name"] == "Updated Streaming"
        assert config["customServices"]["my-streaming"]["domains"] == ["new.com"]

    def test_empty_domains(self, client, tmp_config):
        client.post(
            "/api/custom-services",
            json={
                "id": "empty",
                "name": "Empty",
                "domains": [],
            },
        )
        config = read_config(tmp_config)
        assert config["customServices"]["empty"]["domains"] == []


@pytest.mark.api
class TestCustomServiceDelete:
    def test_delete_service(self, client, tmp_config):
        resp = client.request("DELETE", "/api/custom-services", json={"id": "my-streaming"})
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "my-streaming" not in config["customServices"]

    def test_delete_nonexistent_no_error(self, client):
        resp = client.request("DELETE", "/api/custom-services", json={"id": "nonexistent"})
        assert resp.status_code == 200
