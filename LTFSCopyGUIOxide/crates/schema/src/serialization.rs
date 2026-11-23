//! XML serialization of LTFS file records and fragments.

use quick_xml::Reader;
use quick_xml::Writer;
use quick_xml::escape::escape;
use quick_xml::events::BytesEnd;
use quick_xml::events::BytesStart;
use quick_xml::events::BytesText;
use quick_xml::events::Event;
use std::io::Cursor;
use std::io::Write;
use std::slice;
use std::str;

use super::{
    FileData, LscExtent, LscFileInput, LscUtf16Slice, decode_general_ref, invalid, percent_encode,
    utf16_string,
};

// HPLTFS consumes the index as a line-oriented XML document.  Keep the
// indentation width at zero so every element starts on its own line without
// changing the compact, schema-compatible tag layout.
pub(super) fn new_xml_writer<W: Write>(inner: W) -> Writer<W> {
    Writer::new_with_indent(inner, b' ', 0)
}

pub(super) fn file_data_from_input(input: &LscFileInput) -> Result<FileData, String> {
    let text = |slice: LscUtf16Slice| unsafe { utf16_string(slice.ptr, slice.len) };
    let optional = |slice: LscUtf16Slice| -> Result<Option<String>, String> {
        if slice.ptr.is_null() && slice.len == 0 {
            Ok(None)
        } else {
            text(slice).map(Some)
        }
    };
    let mut result = FileData {
        name: text(input.name)?,
        length: input.length,
        read_only: input.read_only != 0,
        open_for_write: input.open_for_write != 0,
        creation_time: optional(input.creation_time)?,
        change_time: optional(input.change_time)?,
        modify_time: optional(input.modify_time)?,
        access_time: optional(input.access_time)?,
        backup_time: optional(input.backup_time)?,
        file_uid: input.file_uid,
        symlink: optional(input.symlink)?,
        ..Default::default()
    };
    if input.xattr_count > 0 {
        if input.xattrs.is_null() {
            return Err(invalid("xattr input pointer is null"));
        }
        // SAFETY: the FFI contract supplies an array with xattr_count entries.
        let entries = unsafe { slice::from_raw_parts(input.xattrs, input.xattr_count as usize) };
        for entry in entries {
            result.xattrs.push((text(entry.key)?, text(entry.value)?));
        }
    }
    if input.extent_count > 0 {
        if input.extents.is_null() {
            return Err(invalid("extent input pointer is null"));
        }
        // SAFETY: the FFI contract supplies an array with extent_count entries.
        let entries = unsafe { slice::from_raw_parts(input.extents, input.extent_count as usize) };
        result.extents = entries
            .iter()
            .map(|value| LscExtent {
                file_offset: value.file_offset,
                partition: value.partition,
                reserved: 0,
                start_block: value.start_block,
                byte_offset: value.byte_offset,
                byte_count: value.byte_count,
            })
            .collect();
    }
    Ok(result)
}

pub(super) fn write_xml_text<W: Write>(writer: &mut Writer<W>, value: &str) -> Result<(), String> {
    let escaped = escape(value);
    writer
        .write_event(Event::Text(BytesText::from_escaped(escaped)))
        .map_err(|error| error.to_string())
}

pub(super) fn write_xml_element<W: Write>(
    writer: &mut Writer<W>,
    name: &str,
    value: Option<&str>,
) -> Result<(), String> {
    // A name with characters XML cannot carry (control characters; CR would come back as LF) is written
    // percent-encoded with percentencoded="true", as the LTFS format specifies. Other names stay as they are.
    let encoded = match value {
        Some(text) if name == "name" => percent_encode(text),
        _ => None,
    };
    let mut start = BytesStart::new(name);
    if encoded.is_some() {
        start.push_attribute(("percentencoded", "true"));
    }
    writer
        .write_event(Event::Start(start))
        .map_err(|error| error.to_string())?;
    if let Some(value) = encoded.as_deref().or(value) {
        write_xml_text(writer, value)?;
    }
    writer
        .write_event(Event::End(BytesEnd::new(name)))
        .map_err(|error| error.to_string())
}

pub(super) fn serialize_file(value: &FileData) -> Result<Vec<u8>, String> {
    let mut writer = Writer::new(Vec::with_capacity(1024));
    write_file(&mut writer, value)?;
    Ok(writer.into_inner())
}

/// One file record written as events into `writer`: the index writer itself, or a buffer for serialize_file.
pub(super) fn write_file<W: Write>(writer: &mut Writer<W>, value: &FileData) -> Result<(), String> {
    writer
        .write_event(Event::Start(BytesStart::new("file")))
        .map_err(|error| error.to_string())?;
    write_xml_element(writer, "name", Some(&value.name))?;
    write_xml_element(writer, "length", Some(&value.length.to_string()))?;
    write_xml_element(
        writer,
        "readonly",
        Some(if value.read_only { "true" } else { "false" }),
    )?;
    write_xml_element(
        writer,
        "openforwrite",
        Some(if value.open_for_write {
            "true"
        } else {
            "false"
        }),
    )?;
    write_xml_element(writer, "creationtime", value.creation_time.as_deref())?;
    write_xml_element(writer, "changetime", value.change_time.as_deref())?;
    write_xml_element(writer, "modifytime", value.modify_time.as_deref())?;
    write_xml_element(writer, "accesstime", value.access_time.as_deref())?;
    write_xml_element(writer, "backuptime", value.backup_time.as_deref())?;
    write_xml_element(writer, "fileuid", Some(&value.file_uid.to_string()))?;
    writer
        .write_event(Event::Start(BytesStart::new("extendedattributes")))
        .map_err(|error| error.to_string())?;
    for (key, attr_value) in &value.xattrs {
        writer
            .write_event(Event::Start(BytesStart::new("xattr")))
            .map_err(|error| error.to_string())?;
        write_xml_element(writer, "key", Some(key))?;
        write_xml_element(writer, "value", Some(attr_value))?;
        writer
            .write_event(Event::End(BytesEnd::new("xattr")))
            .map_err(|error| error.to_string())?;
    }
    writer
        .write_event(Event::End(BytesEnd::new("extendedattributes")))
        .map_err(|error| error.to_string())?;
    if let Some(symlink) = value.symlink.as_deref() {
        write_xml_element(writer, "symlink", Some(symlink))?;
    }
    writer
        .write_event(Event::Start(BytesStart::new("extentinfo")))
        .map_err(|error| error.to_string())?;
    for extent in &value.extents {
        writer
            .write_event(Event::Start(BytesStart::new("extent")))
            .map_err(|error| error.to_string())?;
        write_xml_element(
            writer,
            "fileoffset",
            Some(&extent.file_offset.to_string()),
        )?;
        write_xml_element(
            writer,
            "partition",
            Some(if extent.partition == 0 { "a" } else { "b" }),
        )?;
        write_xml_element(
            writer,
            "startblock",
            Some(&extent.start_block.to_string()),
        )?;
        write_xml_element(
            writer,
            "byteoffset",
            Some(&extent.byte_offset.to_string()),
        )?;
        write_xml_element(
            writer,
            "bytecount",
            Some(&extent.byte_count.to_string()),
        )?;
        writer
            .write_event(Event::End(BytesEnd::new("extent")))
            .map_err(|error| error.to_string())?;
    }
    writer
        .write_event(Event::End(BytesEnd::new("extentinfo")))
        .map_err(|error| error.to_string())?;
    writer
        .write_event(Event::End(BytesEnd::new("file")))
        .map_err(|error| error.to_string())?;
    Ok(())
}

/// One stored file record (a whole `<file>...</file>` element) into the index writer: exactly the bytes
/// write_fragment writes, without parsing the record into events when it is in the plain form plan_record accepts
/// (records this program stored are). The writer indents with width 0, so write_fragment's output is the record
/// with a line break before every tag except one that follows text; the root start and end tags go through the
/// writer itself, so its own state stays as after write_fragment. Any other record goes to write_fragment.
pub(super) fn write_record<W: Write>(
    writer: &mut Writer<W>,
    bytes: &[u8],
    breaks: &mut Vec<usize>,
) -> Result<(), String> {
    let Some(plan) = plan_record(bytes, breaks) else {
        return write_fragment(writer, bytes);
    };
    // plan_record checked that the record is UTF-8 and where the root tag and its name end.
    let root = str::from_utf8(&bytes[1..plan.root_end - 1]).map_err(|error| error.to_string())?;
    let name = str::from_utf8(&bytes[1..plan.root_name_end]).map_err(|error| error.to_string())?;
    writer
        .write_event(Event::Start(BytesStart::from_content(root, plan.root_name_end - 1)))
        .map_err(|error| error.to_string())?;
    let output = writer.get_mut();
    let mut run = plan.root_end;
    for &position in breaks.iter() {
        output.write_all(&bytes[run..position]).map_err(|error| error.to_string())?;
        output.write_all(b"\n").map_err(|error| error.to_string())?;
        run = position;
    }
    output
        .write_all(&bytes[run..plan.root_end_tag])
        .map_err(|error| error.to_string())?;
    writer
        .write_event(Event::End(BytesEnd::new(name)))
        .map_err(|error| error.to_string())
}

pub(super) struct RecordPlan {
    /// Just past the root start tag's `>`.
    root_end: usize,
    /// Just past the root element's name.
    root_name_end: usize,
    /// Where the root end tag starts (it ends the record).
    root_end_tag: usize,
}

const RECORD_MAX_DEPTH: usize = 16;

/// For a record of the plain form - one root element with start and end tag, then only start, end and empty tags
/// with ASCII names, end tags written `</name>` and matching, quoted attribute values without `<` or `>`, text
/// without `>` whose `&` references all decode, valid UTF-8, the root end tag after a tag - the positions of the
/// tags that get a line break (`breaks`) and of the root tags. None for anything else: comments, CDATA,
/// declarations, other text or names, malformed records.
pub(super) fn plan_record(bytes: &[u8], breaks: &mut Vec<usize>) -> Option<RecordPlan> {
    breaks.clear();
    if bytes.first() != Some(&b'<') || str::from_utf8(bytes).is_err() {
        return None;
    }
    let length = bytes.len();
    let mut stack = [(0usize, 0usize); RECORD_MAX_DEPTH];
    let mut depth = 0usize;
    let mut previous_was_tag = true;
    let mut root: Option<(usize, usize)> = None;
    let mut position = 0usize;
    while position < length {
        if root.is_some() && depth == 0 {
            return None; // bytes after the root element
        }
        if bytes[position] != b'<' {
            // text, up to the next tag; root is set here (the record starts with a tag)
            let start = position;
            while position < length && bytes[position] != b'<' {
                match bytes[position] {
                    b'>' => return None,
                    b'&' => {
                        let name_start = position + 1;
                        let mut end = name_start;
                        while end < length && bytes[end] != b';' && bytes[end] != b'<' {
                            end += 1;
                        }
                        if end >= length || bytes[end] != b';' {
                            return None;
                        }
                        let name = str::from_utf8(&bytes[name_start..end]).ok()?;
                        decode_general_ref(name).ok()?;
                        position = end + 1;
                        continue;
                    }
                    _ => position += 1,
                }
            }
            if position > start {
                previous_was_tag = false;
            }
            continue;
        }
        let tag_start = position;
        match bytes.get(position + 1) {
            Some(b'/') => {
                let name_start = position + 2;
                let mut end = name_start;
                while end < length && is_record_name_byte(bytes[end]) {
                    end += 1;
                }
                if end == name_start || end >= length || bytes[end] != b'>' || depth == 0 {
                    return None;
                }
                let (open_start, open_end) = stack[depth - 1];
                if bytes[open_start..open_end] != bytes[name_start..end] {
                    return None;
                }
                depth -= 1;
                if depth == 0 {
                    if end + 1 != length || !previous_was_tag {
                        return None;
                    }
                    let (root_end, root_name_end) = root?;
                    return Some(RecordPlan {
                        root_end,
                        root_name_end,
                        root_end_tag: tag_start,
                    });
                }
                if previous_was_tag {
                    breaks.push(tag_start);
                }
                previous_was_tag = true;
                position = end + 1;
            }
            Some(_) => {
                let name_start = position + 1;
                let mut end = name_start;
                while end < length && is_record_name_byte(bytes[end]) {
                    end += 1;
                }
                if end == name_start || end >= length {
                    return None;
                }
                let name_end = end;
                if !matches!(bytes[end], b'>' | b'/' | b' ' | b'\t' | b'\r' | b'\n') {
                    return None;
                }
                let mut quote = 0u8;
                while end < length {
                    let byte = bytes[end];
                    if quote != 0 {
                        if byte == quote {
                            quote = 0;
                        } else if byte == b'<' || byte == b'>' {
                            return None;
                        }
                    } else if byte == b'"' || byte == b'\'' {
                        quote = byte;
                    } else if byte == b'<' {
                        return None;
                    } else if byte == b'>' {
                        break;
                    }
                    end += 1;
                }
                if end >= length {
                    return None;
                }
                let empty = bytes[end - 1] == b'/';
                if root.is_none() {
                    if empty || tag_start != 0 {
                        return None;
                    }
                    root = Some((end + 1, name_end));
                } else if previous_was_tag {
                    breaks.push(tag_start);
                }
                if !empty {
                    if depth == RECORD_MAX_DEPTH {
                        return None;
                    }
                    stack[depth] = (name_start, name_end);
                    depth += 1;
                }
                previous_was_tag = true;
                position = end + 1;
            }
            None => return None,
        }
    }
    None // the root element is not closed
}

fn is_record_name_byte(byte: u8) -> bool {
    byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-' | b'.' | b':')
}

pub(super) fn write_fragment<W: Write>(writer: &mut Writer<W>, bytes: &[u8]) -> Result<(), String> {
    let mut reader = Reader::from_reader(Cursor::new(bytes));
    let mut buffer = Vec::new();
    loop {
        let event = reader
            .read_event_into(&mut buffer)
            .map_err(|error| error.to_string())?
            .into_owned();
        buffer.clear();
        match event {
            Event::Start(value) => writer.write_event(Event::Start(value)),
            Event::Empty(value) => writer.write_event(Event::Empty(value)),
            Event::End(value) => writer.write_event(Event::End(value)),
            Event::Text(value) => writer.write_event(Event::Text(value)),
            Event::CData(value) => writer.write_event(Event::CData(value)),
            Event::Comment(_) | Event::PI(_) => Ok(()),
            Event::Decl(_) => return Err(invalid("XML declaration in fragment")),
            Event::DocType(_) => return Err(invalid("unsafe XML construct in fragment")),
            Event::GeneralRef(value) => {
                decode_general_ref(value.as_ref())?;
                // Written as text: the index writer indents, and it puts a line break before a
                // GeneralRef event (and before the end tag after one), so `<name>&apos;x</name>`
                // came out as a name starting with a line break, which Windows cannot restore.
                let name: &str = value.as_ref();
                writer.write_event(Event::Text(BytesText::from_escaped(format!("&{name};"))))
            }
            Event::Eof => break,
        }
        .map_err(|error| error.to_string())?;
    }
    Ok(())
}
