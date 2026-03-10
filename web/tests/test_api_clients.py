"""API tests for client CRUD."""


import pytest

from tests.conftest import read_config


@pytest.mark.api
class TestClientSave:

    def test_create_new_client(self, client, tmp_config):
        resp = client.post("/api/clients", json={
            "name": "iPhone",
            "ids": ["192.168.1.50"],
            "profile": "kids",
        })
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert config["clients"][-1]["name"] == "iPhone"

    def test_update_by_index(self, client, tmp_config):
        resp = client.post("/api/clients", json={
            "index": 0,
            "name": "iPad Pro",
            "ids": ["192.168.1.10"],
            "profile": "adults",
        })
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert config["clients"][0]["name"] == "iPad Pro"
        assert config["clients"][0]["profile"] == "adults"

    def test_out_of_range_index_appends(self, client, tmp_config):
        resp = client.post("/api/clients", json={
            "index": 999,
            "name": "New Device",
            "ids": ["10.0.0.1"],
            "profile": "",
        })
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert config["clients"][-1]["name"] == "New Device"

    def test_multiple_ids(self, client, tmp_config):
        client.post("/api/clients", json={
            "name": "Multi",
            "ids": ["192.168.1.100", "lab.dns.example", "10.0.0.0/8"],
            "profile": "kids",
        })
        config = read_config(tmp_config)
        added = config["clients"][-1]
        assert len(added["ids"]) == 3
        assert "10.0.0.0/8" in added["ids"]

    def test_empty_profile(self, client, tmp_config):
        client.post("/api/clients", json={
            "name": "Unassigned",
            "ids": ["192.168.1.200"],
            "profile": "",
        })
        config = read_config(tmp_config)
        assert config["clients"][-1]["profile"] == ""

    def test_negative_index_appends(self, client, tmp_config):
        initial_count = len(read_config(tmp_config)["clients"])
        client.post("/api/clients", json={
            "index": -1,
            "name": "Negative",
            "ids": ["1.2.3.4"],
            "profile": "",
        })
        config = read_config(tmp_config)
        assert len(config["clients"]) == initial_count + 1


@pytest.mark.api
class TestClientDelete:

    def test_delete_by_index(self, client, tmp_config):
        resp = client.request("DELETE", "/api/clients", json={"index": 0})
        assert resp.status_code == 200
        config = read_config(tmp_config)
        names = [c["name"] for c in config["clients"]]
        assert "iPad" not in names

    def test_delete_out_of_range_no_op(self, client, tmp_config):
        initial = read_config(tmp_config)["clients"].copy()
        client.request("DELETE", "/api/clients", json={"index": 999})
        config = read_config(tmp_config)
        assert len(config["clients"]) == len(initial)

    def test_delete_preserves_order(self, client, tmp_config):
        """Deleting first client shifts second to index 0."""
        client.request("DELETE", "/api/clients", json={"index": 0})
        config = read_config(tmp_config)
        assert config["clients"][0]["name"] == "Laptop"
