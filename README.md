# Welcome to the Oracle Database Metadata Extractor

This project provides a portable command-line tool some metadata of an Oracle
database schema. Metadata of the following objects are extracted: tables, views,
materialized views, sequences, user-defined types, packages, procedures and
functions. Package, procedure and function bodies are not extracted. Wrapped
code is supported.

The tool is not meant to extract all available metadata, but only that are
required for the conversion of Oracle Forms to other technologies.

### Usage

```
ExtractOracleDbMetadata username/password@hostname[:port]/database output-folder
```

Note that `username` and `password` are case-sensitive. A zip file whose name is
of the form `DbMetadata_yyyy-MM-dd_HH.mm.ss.zip` containing the metadata is created
in the `output-folder`. If the `output-folder` does not exist, it is created.

### Releases
