"""Download one completed Kaggle kernel output file without buffering it.

Kaggle output downloads can be several gigabytes.  The normal CLI currently
walks every output file and buffers each response, so this helper locates one
exact output and downloads it as resumable HTTP ranges.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib
import os
from pathlib import Path
import re
import shutil
import time
from concurrent.futures import ThreadPoolExecutor, as_completed

import requests


def find_output_url(kernel: str, file_name: str, page_size: int) -> str:
    module = importlib.import_module("kaggle.api.kaggle_api_extended")
    api = module.KaggleApi()
    api.authenticate()
    owner, slug, _ = api.parse_kernel_string(kernel)
    token = None
    while True:
        request = module.ApiListKernelSessionOutputRequest()
        request.user_name = owner
        request.kernel_slug = slug
        api._set_paging(request, page_size, token)
        with api.build_kaggle_client() as client:
            response = client.kernels.kernels_api_client.list_kernel_session_output(request)
        for item in response.files or []:
            if item.file_name == file_name:
                return item.url
        token = response.next_page_token
        if not token:
            break
    raise FileNotFoundError(f"Kaggle output file was not found: {file_name}")


def get_total_size(url: str) -> int:
    with requests.get(
        url,
        headers={"Range": "bytes=0-0"},
        stream=True,
        timeout=(30, 120),
    ) as response:
        response.raise_for_status()
        if response.status_code != 206:
            raise RuntimeError(f"Kaggle did not honor a byte range: HTTP {response.status_code}")
        content_range = response.headers.get("Content-Range", "")
        match = re.fullmatch(r"bytes 0-0/(\d+)", content_range)
        if not match:
            raise RuntimeError(f"Kaggle returned an invalid Content-Range: {content_range!r}")
        return int(match.group(1))


def download_part(
    url: str,
    part_path: Path,
    start: int,
    end: int,
    total: int,
    attempts: int = 4,
) -> int:
    expected = end - start + 1
    if part_path.exists() and part_path.stat().st_size == expected:
        return expected

    temporary = part_path.with_name(part_path.name + ".part")
    for attempt in range(1, attempts + 1):
        try:
            if temporary.exists():
                temporary.unlink()
            with requests.get(
                url,
                headers={"Range": f"bytes={start}-{end}"},
                stream=True,
                timeout=(30, 180),
            ) as response:
                response.raise_for_status()
                if response.status_code != 206:
                    raise RuntimeError(f"HTTP {response.status_code} for range {start}-{end}")
                content_range = response.headers.get("Content-Range", "")
                if content_range != f"bytes {start}-{end}/{total}":
                    raise RuntimeError(f"unexpected Content-Range for range {start}-{end}: {content_range!r}")
                with temporary.open("wb") as stream:
                    for block in response.iter_content(chunk_size=4 * 1024 * 1024):
                        if block:
                            stream.write(block)
            if temporary.stat().st_size != expected:
                raise RuntimeError(
                    f"short range {start}-{end}: got {temporary.stat().st_size}, expected {expected}"
                )
            os.replace(temporary, part_path)
            return expected
        except Exception:
            if temporary.exists():
                temporary.unlink()
            if attempt == attempts:
                raise
            time.sleep(attempt * 2)
    raise AssertionError("unreachable")


def ranged_download(url: str, destination: Path, chunk_mib: int, workers: int) -> tuple[int, str]:
    destination.parent.mkdir(parents=True, exist_ok=True)
    total = get_total_size(url)
    chunk_size = chunk_mib * 1024 * 1024
    parts_dir = destination.with_name(destination.name + ".parts")
    parts_dir.mkdir(parents=True, exist_ok=True)
    ranges = []
    offset = 0
    index = 0
    while offset < total:
        end = min(total - 1, offset + chunk_size - 1)
        ranges.append((index, offset, end))
        offset = end + 1
        index += 1

    completed = 0
    with ThreadPoolExecutor(max_workers=workers) as executor:
        futures = {
            executor.submit(
                download_part,
                url,
                parts_dir / f"part-{index:05d}.bin",
                start,
                end,
                total,
            ): (index, start, end)
            for index, start, end in ranges
        }
        for future in as_completed(futures):
            index, start, end = futures[future]
            future.result()
            completed += 1
            print(f"downloaded range {completed}/{len(ranges)} ({end + 1}/{total} bytes)", flush=True)

    assembled = destination.with_name(destination.name + ".part")
    with assembled.open("wb") as output:
        for index, _, _ in ranges:
            part_path = parts_dir / f"part-{index:05d}.bin"
            with part_path.open("rb") as part:
                shutil.copyfileobj(part, output, length=4 * 1024 * 1024)
    if assembled.stat().st_size != total:
        raise RuntimeError(f"assembled file has {assembled.stat().st_size} bytes, expected {total}")

    digest = hashlib.sha256()
    with assembled.open("rb") as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            digest.update(block)
    os.replace(assembled, destination)
    return total, digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--kernel", required=True)
    parser.add_argument("--file-name", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--page-size", type=int, default=200)
    parser.add_argument("--chunk-mib", type=int, default=32)
    parser.add_argument("--workers", type=int, default=6)
    args = parser.parse_args()
    if args.page_size < 1 or args.page_size > 200:
        parser.error("--page-size must be between 1 and 200")
    if args.chunk_mib < 1 or args.chunk_mib > 256:
        parser.error("--chunk-mib must be between 1 and 256")
    if args.workers < 1 or args.workers > 16:
        parser.error("--workers must be between 1 and 16")
    if args.output.exists():
        parser.error(f"refusing to overwrite existing output: {args.output}")

    url = find_output_url(args.kernel, args.file_name, args.page_size)
    total, digest = ranged_download(url, args.output, args.chunk_mib, args.workers)
    print(f"downloaded {args.file_name}: bytes={total} sha256={digest} path={args.output}")


if __name__ == "__main__":
    main()
