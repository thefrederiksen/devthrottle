# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec file for cc-transcribe."""

from pathlib import Path

block_cipher = None

# Get the spec file directory
spec_path = Path(SPECPATH)

a = Analysis(
    [str(spec_path / 'main.py')],
    pathex=[SPECPATH, str(spec_path / 'src')],
    binaries=[],
    datas=[],
    hiddenimports=[
        'typer',
        'rich',
        'rich.console',
        'rich.table',
        'rich.panel',
        'rich.text',
        'rich.progress',
        'openai',
        'cv2',
        'PIL',
        'PIL.Image',
        'numpy',
        'cli',
        'ffmpeg',
        'screenshots',
        'transcriber',
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name='cc-transcribe',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=None,
)
