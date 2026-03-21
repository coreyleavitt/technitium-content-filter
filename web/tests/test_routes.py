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

    def test_profile_detail(self, client):
        resp = client.get("/profiles/kids")
        assert resp.status_code == 200
        assert "text/html" in resp.headers["content-type"]
        assert "kids" in resp.text

    def test_profile_detail_nonexistent_redirects(self, client):
        resp = client.get("/profiles/nonexistent", follow_redirects=False)
        assert resp.status_code == 302
        assert "/profiles" in resp.headers["location"]

    def test_clients(self, client):
        resp = client.get("/clients")
        assert resp.status_code == 200
        assert "iPad" in resp.text
        assert "Laptop" in resp.text

    def test_settings(self, client):
        resp = client.get("/settings")
        assert resp.status_code == 200
        assert "text/html" in resp.headers["content-type"]
        assert "Settings" in resp.text

    def test_services_redirects_to_settings(self, client):
        resp = client.get("/services", follow_redirects=False)
        assert resp.status_code == 301
        assert "/settings" in resp.headers["location"]

    def test_filters_blocklists_redirects(self, client):
        resp = client.get("/filters/blocklists", follow_redirects=False)
        assert resp.status_code == 301
        assert "/settings" in resp.headers["location"]

    def test_filters_allowlists_redirects(self, client):
        resp = client.get("/filters/allowlists", follow_redirects=False)
        assert resp.status_code == 301
        assert "/profiles" in resp.headers["location"]

    def test_filters_services_redirects(self, client):
        resp = client.get("/filters/services", follow_redirects=False)
        assert resp.status_code == 301
        assert "/settings" in resp.headers["location"]

    def test_filters_rules_redirects(self, client):
        resp = client.get("/filters/rules", follow_redirects=False)
        assert resp.status_code == 301
        assert "/profiles" in resp.headers["location"]

    def test_filters_regex_redirects(self, client):
        resp = client.get("/filters/regex", follow_redirects=False)
        assert resp.status_code == 301
        assert "/profiles" in resp.headers["location"]

    def test_filters_rewrites_redirects(self, client):
        resp = client.get("/filters/rewrites", follow_redirects=False)
        assert resp.status_code == 301
        assert "/profiles" in resp.headers["location"]


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

    def test_settings_empty(self, client_empty):
        resp = client_empty.get("/settings")
        assert resp.status_code == 200
