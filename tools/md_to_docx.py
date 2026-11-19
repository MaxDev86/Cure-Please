#!/usr/bin/env python3
"""Converts the QA markdown guides in docs/ to DOCX (same folder, same name).

Handles the markdown subset the guides use: #/##/### headings, **bold**, - and 1.
lists, | tables |, > quotes, and --- rules. Everything else becomes body text.

Usage: python tools/md_to_docx.py [file.md ...]   (default: docs/qa-*.md)
"""
import glob
import os
import re
import sys

from docx import Document
from docx.shared import Pt

BOLD_RE = re.compile(r"\*\*(.+?)\*\*")


def add_runs(paragraph, text):
    """Adds text to a paragraph, honoring **bold** spans."""
    pos = 0
    for m in BOLD_RE.finditer(text):
        if m.start() > pos:
            paragraph.add_run(text[pos:m.start()])
        paragraph.add_run(m.group(1)).bold = True
        pos = m.end()
    if pos < len(text):
        paragraph.add_run(text[pos:])


def convert(md_path, docx_path):
    doc = Document()
    for style_name in ("Normal", "List Bullet", "List Number"):
        style = doc.styles[style_name]
        style.font.name = "Calibri"
        style.font.size = Pt(11)
        pf = style.paragraph_format
        pf.line_spacing = 1.0
        pf.space_before = Pt(0)
        pf.space_after = Pt(2)
    for style_name in ("Heading 1", "Heading 2", "Heading 3", "Heading 4"):
        pf = doc.styles[style_name].paragraph_format
        pf.space_before = Pt(6)
        pf.space_after = Pt(2)

    with open(md_path, encoding="utf-8") as f:
        lines = f.read().splitlines()

    i = 0
    while i < len(lines):
        line = lines[i].rstrip()
        stripped = line.strip()

        if not stripped or stripped == "---":
            i += 1
            continue

        if stripped.startswith("|"):
            table_lines = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                table_lines.append(lines[i].strip())
                i += 1
            rows = [[c.strip() for c in tl.strip("|").split("|")] for tl in table_lines]
            rows = [r for r in rows if not all(set(c) <= set("-: ") for c in r)]
            if rows:
                table = doc.add_table(rows=len(rows), cols=len(rows[0]))
                table.style = "Table Grid"
                for r, row in enumerate(rows):
                    for c, cell in enumerate(row):
                        cellp = table.rows[r].cells[c].paragraphs[0]
                        add_runs(cellp, cell)
                        if r == 0:
                            for run in cellp.runs:
                                run.bold = True
            continue

        if stripped.startswith(">"):
            add_runs(doc.add_paragraph(stripped[1:].strip(), style="Intense Quote"), "")
            i += 1
            continue

        m = re.match(r"^(#{1,4})\s+(.*)$", stripped)
        if m:
            level = len(m.group(1))
            heading = doc.add_heading("", level=min(level, 4))
            add_runs(heading, m.group(2))
            i += 1
            continue

        m = re.match(r"^[-*]\s+(.*)$", stripped)
        if m:
            add_runs(doc.add_paragraph(style="List Bullet"), m.group(1))
            i += 1
            continue

        m = re.match(r"^\d+\.\s+(.*)$", stripped)
        if m:
            add_runs(doc.add_paragraph(style="List Number"), m.group(1))
            i += 1
            continue

        add_runs(doc.add_paragraph(), stripped)
        i += 1

    doc.save(docx_path)
    print("wrote", docx_path)


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    paths = sys.argv[1:] or sorted(glob.glob(os.path.join(root, "docs", "qa-*.md")))
    for md in paths:
        convert(md, os.path.splitext(md)[0] + ".docx")


if __name__ == "__main__":
    main()
