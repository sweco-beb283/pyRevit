# -*- coding: utf-8 -*-
"""Builtin routes API.

This module also provides the API object to be used by third-party
api developers to define new apis
"""
#pylint: disable=invalid-name
from pyrevit import HOST_APP
from pyrevit.coreutils.logger import get_logger
from pyrevit.loader import sessioninfo

from pyrevit import routes
from pyrevit.routes.server import serverinfo


# mlogger = get_logger(__name__)


# =============================================================================
# routes server base API
routes_api = routes.API('routes')
# =============================================================================


# GET /status
@routes_api.route('/status', methods=['GET'])
def get_status():
    """Get server status."""
    return {
        "host": HOST_APP.pretty_name,
        "username": HOST_APP.username,
        "session_id": sessioninfo.get_session_uuid(),
        }

# GET /sisters
@routes_api.route('/sisters', methods=['GET'])
def get_sisters():
    """Get other servers running on the same machine."""
    return [x.get_cache_data() for x in serverinfo.get_registered_servers()]


# GET /sisters/<int:year>
@routes_api.route('/sisters/<int:version>', methods=['GET'])
def get_sisters_by_year(version):
    """Get servers of specific version, running on the same machine."""
    return [x.get_cache_data() for x in serverinfo.get_registered_servers()
            if int(x.version) == version]


# GET /commands
@routes_api.route('/commands', methods=['GET'])
def get_commands(uiapp):
    """List all loaded pyRevit commands with their control IDs."""
    from pyrevit.loader import sessionmgr
    commands = sessionmgr.find_all_commands(cache=True)
    return [
        {
            "name": cmd.name,
            "control_id": cmd.control_id,
            "bundle": cmd.bundle,
            "extension": cmd.extension,
            "unique_id": cmd.unique_id,
        }
        for cmd in commands
    ]


# POST /commands/run
@routes_api.route('/commands/run', methods=['POST'])
def run_command(request, uiapp):
    """Run a pyRevit command by control ID.

    Request body (JSON): {
        "control_id": "CustomCtrl_%CustomCtrl_%...",
        "wait": true  (optional, default true — set false for fire-and-forget)
    }
    """
    from pyrevit.loader import sessionmgr
    from pyrevit.api import UI
    from datetime import datetime
    data = request.data or {}
    control_id = data.get('control_id', None)
    wait = data.get('wait', True)
    mlogger = get_logger("route-command-runner")

    if not control_id:
        return {"error": "control_id is required in request body"}

    # fire-and-forget via PostCommand
    if not wait:
        command_id = UI.RevitCommandId.LookupCommandId(control_id)
        if command_id is None:
            return {"error": "Command not found: {}".format(control_id)}
        uiapp.PostCommand(command_id)
        return {"status": "posted", "control_id": control_id}

    # synchronous execution with result
    cmd = next((c for c in sessionmgr.find_all_commands()
                if c.control_id == control_id), None)
    if cmd is None:
        return {"error": "Command not found: {}".format(control_id)}

    now = datetime.now()
    mlogger.debug('[SCRIPT:START] command=%s controlId=%s', cmd.unique_id, control_id)
    mlogger.info('[SCRIPT:START] command=%s controlId=%s', cmd.unique_id, control_id)
    result = sessionmgr.execute_command_cls(cmd.extcmd_type)
    mlogger.debug('[SCRIPT:END] command=%s controlId=%s result=%s', cmd.unique_id, control_id, result)
    mlogger.info('[SCRIPT:END] command=%s controlId=%s result=%s', cmd.unique_id, control_id, result)
    return {"status": str(result), "control_id": control_id, "execution_time": str(datetime.now() - now)}
