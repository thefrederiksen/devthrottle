"""FastAPI application for CC Director Gateway."""

import logging
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import HTMLResponse
from fastapi.staticfiles import StaticFiles
from fastapi.templating import Jinja2Templates

from ..database import Database
from .routes import jobs, runs, system, websocket

logger = logging.getLogger("cc_director.gateway")


def create_app(db: Database, running_jobs: Optional[set] = None) -> FastAPI:
    """
    Create and configure the FastAPI application.

    Args:
        db: Database instance for job/run data
        running_jobs: Optional set of currently running job IDs

    Returns:
        Configured FastAPI application
    """
    app = FastAPI(
        title="DevThrottle",
        description="Job scheduler dashboard and REST API",
        version="0.1.0",
        docs_url="/api/docs",
        redoc_url="/api/redoc",
    )

    # Store database in app state
    app.state.db = db
    app.state.running_jobs = running_jobs or set()

    # CORS middleware - allow localhost origins for development
    app.add_middleware(
        CORSMiddleware,
        allow_origins=[
            "http://localhost:6060",
            "http://127.0.0.1:6060",
        ],
        allow_credentials=True,
        allow_methods=["*"],
        allow_headers=["*"],
    )

    # Static files and templates
    gateway_dir = Path(__file__).parent
    static_dir = gateway_dir / "static"
    templates_dir = gateway_dir / "templates"

    # Create directories if they don't exist
    static_dir.mkdir(exist_ok=True)
    templates_dir.mkdir(exist_ok=True)

    if static_dir.exists():
        app.mount("/static", StaticFiles(directory=static_dir), name="static")

    templates = Jinja2Templates(directory=str(templates_dir))
    app.state.templates = templates

    # Include API routers
    app.include_router(jobs.router, prefix="/api/jobs", tags=["Jobs"])
    app.include_router(runs.router, prefix="/api/runs", tags=["Runs"])
    app.include_router(system.router, prefix="/api", tags=["System"])
    app.include_router(websocket.router, tags=["WebSocket"])

    # Dashboard routes
    @app.get("/", response_class=HTMLResponse)
    async def dashboard(request: Request):
        """Dashboard home page."""
        return templates.TemplateResponse(
            "dashboard.html",
            {"request": request}
        )

    @app.get("/jobs", response_class=HTMLResponse)
    async def jobs_page(request: Request):
        """Jobs list page."""
        return templates.TemplateResponse(
            "jobs.html",
            {"request": request}
        )

    @app.get("/jobs/{name}", response_class=HTMLResponse)
    async def job_detail_page(request: Request, name: str):
        """Job detail page."""
        return templates.TemplateResponse(
            "job_detail.html",
            {"request": request, "job_name": name}
        )

    @app.get("/runs", response_class=HTMLResponse)
    async def runs_page(request: Request):
        """Runs list page."""
        return templates.TemplateResponse(
            "runs.html",
            {"request": request}
        )

    @app.get("/runs/{run_id}", response_class=HTMLResponse)
    async def run_detail_page(request: Request, run_id: int):
        """Run detail page."""
        return templates.TemplateResponse(
            "run_detail.html",
            {"request": request, "run_id": run_id}
        )

    logger.info("Gateway application created")
    return app
