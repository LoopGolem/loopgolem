#!/usr/bin/env python3
"""Probe Codex app-server thread/fork model inheritance without starting a model turn.

This script intentionally performs only initialize, thread/start, and thread/fork
JSON-RPC operations. It never calls turn/start, so it should not request model
inference or consume turn tokens.

The comparison reproduces the old LoopGolem fork shape (no explicit model on
thread/fork) against the pinned shape (model='gpt-6-luna') from the same fresh
Luna HIGH parent.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from typing import Any

MODEL = "gpt-6-luna"


def send(stream, payload: dict[str, Any]) -> None:
    stream.write(json.dumps(payload, separators=(",", ":")) + "\n")
    stream.flush()


def request(
    proc: subprocess.Popen[str],
    request_id: int,
    method: str,
    params: dict[str, Any],
) -> dict[str, Any]:
    assert proc.stdin is not None
    assert proc.stdout is not None

    send(
        proc.stdin,
        {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": params,
        },
    )

    while True:
        line = proc.stdout.readline()
        if line == "":
            stderr = ""
            if proc.stderr is not None:
                stderr = proc.stderr.read()
            raise RuntimeError(
                f"app-server closed before response to {method}. stderr={stderr!r}"
            )

        message = json.loads(line)
        if message.get("id") != request_id:
            continue

        if "error" in message:
            raise RuntimeError(
                f"{method} failed: {json.dumps(message['error'], ensure_ascii=False)}"
            )

        result = message.get("result")
        if not isinstance(result, dict):
            raise RuntimeError(f"{method} returned a non-object result: {result!r}")

        return result


def notify(
    proc: subprocess.Popen[str],
    method: str,
    params: dict[str, Any] | None = None,
) -> None:
    assert proc.stdin is not None
    payload: dict[str, Any] = {
        "jsonrpc": "2.0",
        "method": method,
    }
    if params is not None:
        payload["params"] = params
    send(proc.stdin, payload)


def describe(result: dict[str, Any]) -> dict[str, Any]:
    thread = result.get("thread")
    thread_id = thread.get("id") if isinstance(thread, dict) else None
    return {
        "threadId": thread_id,
        "model": result.get("model"),
        "modelProvider": result.get("modelProvider"),
        "reasoningEffort": result.get("reasoningEffort"),
        "cwd": result.get("cwd"),
    }


def main() -> int:
    command = [
        "codex",
        "--enable",
        "reasoning_effort_override",
        "--disable",
        "apps",
        "--disable",
        "plugins",
        "--disable",
        "multi_agent",
        "--disable",
        "memories",
        "app-server",
        "--listen",
        "stdio://",
    ]

    proc = subprocess.Popen(
        command,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1,
    )

    try:
        request(
            proc,
            1,
            "initialize",
            {
                "clientInfo": {
                    "name": "loopgolem-fork-model-probe",
                    "title": "LoopGolem fork model probe",
                    "version": "0.1",
                },
                "capabilities": {
                    "experimentalApi": False,
                    "requestAttestation": False,
                },
            },
        )
        notify(proc, "initialized")

        cwd = os.getcwd()
        parent = request(
            proc,
            2,
            "thread/start",
            {
                "model": MODEL,
                "cwd": cwd,
                "approvalPolicy": "never",
                "sandbox": "workspace-write",
                "config": {
                    "model_reasoning_effort": "high",
                    "sandbox_workspace_write.network_access": False,
                },
                "ephemeral": True,
            },
        )

        parent_thread = parent.get("thread")
        if not isinstance(parent_thread, dict) or not parent_thread.get("id"):
            raise RuntimeError("thread/start returned no parent thread id")

        parent_id = str(parent_thread["id"])

        common_fork = {
            "threadId": parent_id,
            "cwd": cwd,
            "approvalPolicy": "never",
            "sandbox": "workspace-write",
            "ephemeral": True,
            "excludeTurns": True,
        }

        old_style = request(
            proc,
            3,
            "thread/fork",
            dict(common_fork),
        )

        pinned = request(
            proc,
            4,
            "thread/fork",
            {
                **common_fork,
                "model": MODEL,
            },
        )

        summary = {
            "modelTurnStarted": False,
            "parent": describe(parent),
            "oldStyleForkWithoutModel": describe(old_style),
            "pinnedForkWithModel": describe(pinned),
        }

        parent_model = summary["parent"]["model"]
        old_model = summary["oldStyleForkWithoutModel"]["model"]
        pinned_model = summary["pinnedForkWithModel"]["model"]

        summary["oldStyleChangedModel"] = old_model != parent_model
        summary["pinnedMatchesRequestedModel"] = pinned_model == MODEL

        print(json.dumps(summary, indent=2, ensure_ascii=False))

        if parent_model != MODEL:
            print(
                f"ERROR: parent thread is {parent_model!r}, expected {MODEL!r}.",
                file=sys.stderr,
            )
            return 2

        if pinned_model != MODEL:
            print(
                f"ERROR: pinned fork is {pinned_model!r}, expected {MODEL!r}.",
                file=sys.stderr,
            )
            return 3

        return 0
    finally:
        if proc.stdin is not None:
            try:
                proc.stdin.close()
            except Exception:
                pass

        try:
            proc.wait(timeout=3)
        except subprocess.TimeoutExpired:
            proc.terminate()
            try:
                proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()


if __name__ == "__main__":
    raise SystemExit(main())
