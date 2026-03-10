"""Page route rendering tests."""

import pytest


@pytest.mark.route
class TestPageRoutes:
    def test_dashboard(self, client):
        resp = client.get("/")
        assert resp.status_code == 200
        assert "text/html" in resp.headers["content-type"]
        # Verify template renders config data
        assert "kids" in resp.text
        assert "adults" in resp.text

    def test_profiles(self, client):
        resp = client.get("/profiles")
        assert resp.status_code == 200
        assert "text/html" in resp.headers["content-type"]
        assert "kids" in resp.text
        assert "adults" in resp.text

    def test_clients(self, client):
        resp = client.get("/clients")
        assert resp.status_code == 200
        assert "iPad" in resp.text
        assert "Laptop" in resp.text

    def test_services_redirects(self, client):
        resp = client.get("/services", follow_redirects=False)
        assert resp.status_code == 301
        assert "/filters/services" in resp.headers["location"]

    def test_filters_blocklists(self, client):
        resp = client.get("/filters/blocklists")
        assert resp.status_code == 200
        assert "example.com/list.txt" in resp.text

    def test_filters_allowlists(self, client):
        resp = client.get("/filters/allowlists")
        assert resp.status_code == 200
        assert "kids" in resp.text

    def test_filters_services(self, client):
        resp = client.get("/filters/services")
        assert resp.status_code == 200
        assert "youtube" in resp.text.lower() or "YouTube" in resp.text

    def test_filters_rules(self, client):
        resp = client.get("/filters/rules")
        assert resp.status_code == 200
        assert "kids" in resp.text

    def test_filters_rewrites(self, client):
        resp = client.get("/filters/rewrites")
        assert resp.status_code == 200
        assert "kids" in resp.text


@pytest.mark.route
class TestApiConfigRoute:
    def test_get_config(self, client):
        resp = client.get("/api/config")
        assert resp.status_code == 200
        data = resp.json()
        assert "enableBlocking" in data
        assert "profiles" in data

    def test_set_config(self, client):
        resp = client.post(
            "/api/config",
            json={
                "enableBlocking": False,
                "profiles": {},
                "clients": [],
            },
        )
        assert resp.status_code == 200
        assert resp.json()["ok"] is True

    def test_get_config_empty(self, client_empty):
        resp = client_empty.get("/api/config")
        assert resp.status_code == 200
        data = resp.json()
        assert data["profiles"] == {}
        assert data["clients"] == []


@pytest.mark.route
class TestEmptyConfig:
    def test_dashboard_empty(self, client_empty):
        resp = client_empty.get("/")
        assert resp.status_code == 200

    def test_profiles_empty(self, client_empty):
        resp = client_empty.get("/profiles")
        assert resp.status_code == 200

    def test_clients_empty(self, client_empty):
        resp = client_empty.get("/clients")
        assert resp.status_code == 200

    def test_filters_blocklists_empty(self, client_empty):
        resp = client_empty.get("/filters/blocklists")
        assert resp.status_code == 200

    def test_filters_allowlists_empty(self, client_empty):
        resp = client_empty.get("/filters/allowlists")
        assert resp.status_code == 200

    def test_filters_services_empty(self, client_empty):
        resp = client_empty.get("/filters/services")
        assert resp.status_code == 200

    def test_filters_rules_empty(self, client_empty):
        resp = client_empty.get("/filters/rules")
        assert resp.status_code == 200

    def test_filters_rewrites_empty(self, client_empty):
        resp = client_empty.get("/filters/rewrites")
        assert resp.status_code == 200
