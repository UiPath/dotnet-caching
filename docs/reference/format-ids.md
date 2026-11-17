# Format ids

A stored value starts with a format id, so a reader can tell which serializer wrote it; JSON has no header.
`AddValueSerializer(name, formatIds, factory)` declares the ids a serializer owns. The library checks the range and that no two serializers on one builder claim the same id.
It checks one builder, including the serializers a named stack inherits, and cannot check across processes, so an id is yours only if it has a row here, added by pull request.

| Range | Use |
|---|---|
| `0x01`-`0x0F`, except `0x09`, `0x0A`, `0x0D` | Allocated: an id is taken only by a row in the table below |
| `0x10`-`0x1E` | Application-private serializers |
| `0x100`-`0x1FF` | Two-byte ids, written as `0x1F` then the low byte: published plugins by convention in `0x100`-`0x17F`, application-private in `0x180`-`0x1FF` |

`0x09`, `0x0A` and `0x0D` are never ids: they begin JSON whitespace. Value ids and event ids are separate namespaces, so one number may be both. An event id must be one byte, because an event body starts with it: `0x01`-`0x0F` allocated, `0x10`-`0x1E` application-private. The `0x100`-`0x17F` advice for published plugins applies to value ids only.

A published plugin makes its ids an option with the row as the default, so an application can move a plugin whose ids clash with its own.

## Allocation

| Id | Kind | Owner | Name |
|---|---|---|---|
| `0x02` | Value | `UiPath.Caching.MessagePack` | `MessagePackFormats.Value` |
| `0x03` | Value | `UiPath.Caching.MessagePack` | `MessagePackFormats.ValueLz4` |
| `0x02` | Event | `UiPath.Caching.MessagePack` | `MessagePackFormats.Event` |
