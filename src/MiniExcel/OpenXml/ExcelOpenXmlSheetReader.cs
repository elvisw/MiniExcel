﻿using MiniExcelLibs.Utils;
using MiniExcelLibs.Zip;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;

namespace MiniExcelLibs.OpenXml
{
    internal class ExcelOpenXmlSheetReader : IExcelReader
    {
        private static readonly string[] _ns = { Config.SpreadsheetmlXmlns, Config.SpreadsheetmlXmlStrictns };
        private static readonly string[] _relationshiopNs = { Config.SpreadsheetmlXmlRelationshipns, Config.SpreadsheetmlXmlStrictRelationshipns };
        private List<SheetRecord> _sheetRecords;
        private IList<string> _sharedStrings;
        private MergeCells _mergeCells;
        private ExcelOpenXmlStyles _style;
        private readonly ExcelOpenXmlZip _archive;
        private OpenXmlConfiguration _config;
        private static readonly XmlReaderSettings _xmlSettings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            XmlResolver = null,
        };

        public ExcelOpenXmlSheetReader(Stream stream,IConfiguration configuration)
        {
            _archive = new ExcelOpenXmlZip(stream);
            _config = (OpenXmlConfiguration)configuration ?? OpenXmlConfiguration.DefaultConfig;
        }

        public IEnumerable<IDictionary<string, object>> Query(bool useHeaderRow, string sheetName, string startCell)
        {
            if (!ReferenceHelper.ParseReference(startCell, out var startColumnIndex, out var startRowIndex))
                throw new InvalidDataException($"startCell {startCell} is Invalid");
            startColumnIndex--; startRowIndex--;

            //TODO:need to optimize
            SetSharedStrings();

            // if sheets count > 1 need to read xl/_rels/workbook.xml.rels  
            var sheets = _archive.entries.Where(w => w.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                || w.FullName.StartsWith("/xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
            );
            ZipArchiveEntry sheetEntry = null;
            if (sheetName != null)
            {
                SetWorkbookRels(_archive.entries);
                var s = _sheetRecords.SingleOrDefault(_ => _.Name == sheetName);
                if (s == null)
                    throw new InvalidOperationException("Please check sheetName/Index is correct");
                sheetEntry = sheets.Single(w => w.FullName == $"xl/{s.Path}" || w.FullName == $"/xl/{s.Path}" || w.FullName == s.Path || s.Path == $"/{w.FullName}");
            }
            else if (sheets.Count() > 1)
            {
                SetWorkbookRels(_archive.entries);
                var s = _sheetRecords[0];
                sheetEntry = sheets.Single(w => w.FullName == $"xl/{s.Path}" || w.FullName == $"/xl/{s.Path}");
            }
            else
                sheetEntry = sheets.Single();


            #region MergeCells
            if (_config.FillMergedCells)
            {
                _mergeCells = new MergeCells();
                using (var sheetStream = sheetEntry.Open())
                using (XmlReader reader = XmlReader.Create(sheetStream, _xmlSettings))
                {
                    if (!XmlReaderHelper.IsStartElement(reader, "worksheet", _ns))
                        yield break;
                    while (reader.Read())
                    {
                        if (XmlReaderHelper.IsStartElement(reader, "mergeCells", _ns))
                        {
                            if (!XmlReaderHelper.ReadFirstContent(reader))
                                yield break;
                            while (!reader.EOF)
                            {
                                if (XmlReaderHelper.IsStartElement(reader, "mergeCell", _ns))
                                {
                                    var @ref = reader.GetAttribute("ref");
                                    var refs = @ref.Split(':');
                                    if (refs.Length == 1)
                                        continue;

                                    ReferenceHelper.ParseReference(refs[0], out var x1, out var y1);
                                    ReferenceHelper.ParseReference(refs[1], out var x2, out var y2);

                                    _mergeCells.MergesValues.Add(refs[0], null);

                                    // foreach range
                                    var isFirst = true;
                                    for (int x = x1; x <= x2; x++)
                                    {
                                        for (int y = y1; y <= y2; y++)
                                        {
                                            if (!isFirst)
                                                _mergeCells.MergesMap.Add(ReferenceHelper.ConvertXyToCell(x, y), refs[0]);
                                            isFirst = false;
                                        }
                                    }

                                    XmlReaderHelper.SkipContent(reader);
                                }
                                else if (!XmlReaderHelper.SkipContent(reader))
                                {
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            #endregion




            // TODO: need to optimize performance
            var withoutCR = false;
            var maxRowIndex = -1;
            var maxColumnIndex = -1;

            //Q. why need 3 times openstream merge one open read? A. no, zipstream can't use position = 0
            using (var sheetStream = sheetEntry.Open())
            using (XmlReader reader = XmlReader.Create(sheetStream, _xmlSettings))
            {
                while (reader.Read())
                {
                    if (XmlReaderHelper.IsStartElement(reader, "c", _ns))
                    {
                        var r = reader.GetAttribute("r");
                        if (r != null)
                        {
                            if (ReferenceHelper.ParseReference(r, out var column, out var row))
                            {
                                column--;
                                row--;
                                maxRowIndex = Math.Max(maxRowIndex, row);
                                maxColumnIndex = Math.Max(maxColumnIndex, column);
                            }
                        }
                        else
                        {
                            withoutCR = true;
                            break;
                        }
                    }
                    //this method logic depends on dimension to get maxcolumnIndex, if without dimension then it need to foreach all rows first time to get maxColumn and maxRowColumn
                    else if (XmlReaderHelper.IsStartElement(reader, "dimension", _ns))
                    {
                        var @ref = reader.GetAttribute("ref");
                        if (string.IsNullOrEmpty(@ref))
                            throw new InvalidOperationException("Without sheet dimension data");
                        var rs = @ref.Split(':');
                        // issue : https://github.com/shps951023/MiniExcel/issues/102
                        if (ReferenceHelper.ParseReference(rs.Length == 2 ? rs[1] : rs[0], out int cIndex, out int rIndex))
                        {
                            maxColumnIndex = cIndex - 1;
                            maxRowIndex = rIndex - 1;
                            break;
                        }
                        else
                            throw new InvalidOperationException("Invaild sheet dimension start data");
                    }
                }
            }

            if (withoutCR)
            {
                using (var sheetStream = sheetEntry.Open())
                using (XmlReader reader = XmlReader.Create(sheetStream, _xmlSettings))
                {
                    if (!XmlReaderHelper.IsStartElement(reader, "worksheet", _ns))
                        yield break;
                    if (!XmlReaderHelper.ReadFirstContent(reader))
                        yield break;
                    while (!reader.EOF)
                    {
                        if (XmlReaderHelper.IsStartElement(reader, "sheetData", _ns))
                        {
                            if (!XmlReaderHelper.ReadFirstContent(reader))
                                continue;

                            while (!reader.EOF)
                            {
                                if (XmlReaderHelper.IsStartElement(reader, "row", _ns))
                                {
                                    maxRowIndex++;

                                    if (!XmlReaderHelper.ReadFirstContent(reader))
                                        continue;

                                    //Cells
                                    {
                                        var cellIndex = -1;
                                        while (!reader.EOF)
                                        {
                                            if (XmlReaderHelper.IsStartElement(reader, "c", _ns))
                                            {
                                                cellIndex++;
                                                maxColumnIndex = Math.Max(maxColumnIndex, cellIndex);
                                            }


                                            if (!XmlReaderHelper.SkipContent(reader))
                                                break;
                                        }
                                    }
                                }
                                else if (!XmlReaderHelper.SkipContent(reader))
                                {
                                    break;
                                }
                            }
                        }
                        else if (!XmlReaderHelper.SkipContent(reader))
                        {
                            break;
                        }
                    }

                }
            }




            using (var sheetStream = sheetEntry.Open())
            using (XmlReader reader = XmlReader.Create(sheetStream, _xmlSettings))
            {
                if (!XmlReaderHelper.IsStartElement(reader, "worksheet", _ns))
                    yield break;

                if (!XmlReaderHelper.ReadFirstContent(reader))
                    yield break;

                while (!reader.EOF)
                {
                    if (XmlReaderHelper.IsStartElement(reader, "sheetData", _ns))
                    {
                        if (!XmlReaderHelper.ReadFirstContent(reader))
                            continue;

                        Dictionary<int, string> headRows = new Dictionary<int, string>();
                        int rowIndex = -1;
                        int nextRowIndex = 0;
                        bool isFirstRow = true;
                        while (!reader.EOF)
                        {
                            if (XmlReaderHelper.IsStartElement(reader, "row", _ns))
                            {
                                nextRowIndex = rowIndex + 1;
                                if (int.TryParse(reader.GetAttribute("r"), out int arValue))
                                    rowIndex = arValue - 1; // The row attribute is 1-based
                                else
                                    rowIndex++;

                                // row -> c
                                if (!XmlReaderHelper.ReadFirstContent(reader))
                                    continue;

                                // startcell pass rows
                                if (rowIndex < startRowIndex)
                                {
                                    XmlReaderHelper.SkipToNextSameLevelDom(reader);
                                    continue;
                                }


                                // fill empty rows
                                if (!(nextRowIndex < startRowIndex))
                                {
                                    if (nextRowIndex < rowIndex)
                                    {
                                        for (int i = nextRowIndex; i < rowIndex; i++)
                                        {
                                            yield return GetCell(useHeaderRow, maxColumnIndex, headRows, startColumnIndex);
                                        }
                                    }
                                }

                                // Set Cells
                                {
                                    var cell = GetCell(useHeaderRow, maxColumnIndex, headRows, startColumnIndex);
                                    var columnIndex = withoutCR ? -1 : 0;
                                    while (!reader.EOF)
                                    {
                                        if (XmlReaderHelper.IsStartElement(reader, "c", _ns))
                                        {
                                            var aS = reader.GetAttribute("s");
                                            var aR = reader.GetAttribute("r");
                                            var aT = reader.GetAttribute("t");
                                            var cellValue = ReadCellAndSetColumnIndex(reader, ref columnIndex, withoutCR, startColumnIndex, aR, aT);

                                            if (_config.FillMergedCells)
                                            {
                                                if (_mergeCells.MergesValues.ContainsKey(aR))
                                                {
                                                    _mergeCells.MergesValues[aR] = cellValue;
                                                }
                                                else if (_mergeCells.MergesMap.ContainsKey(aR))
                                                {
                                                    var mergeKey = _mergeCells.MergesMap[aR];
                                                    object mergeValue = null;
                                                    if(_mergeCells.MergesValues.ContainsKey(mergeKey))
                                                        mergeValue = _mergeCells.MergesValues[mergeKey];
                                                    cellValue = mergeValue;
                                                }
                                            }

                                            if (columnIndex < startColumnIndex)
                                                continue;

                                            if (!string.IsNullOrEmpty(aS)) // if c with s meaning is custom style need to check type by xl/style.xml
                                            {
                                                int xfIndex = -1;
                                                if (int.TryParse(aS, NumberStyles.Any, CultureInfo.InvariantCulture, out var styleIndex))
                                                    xfIndex = styleIndex;

                                                // only when have s attribute then load styles xml data
                                                if (_style == null)
                                                    _style = new ExcelOpenXmlStyles(_archive);
      
                                                cellValue = _style.ConvertValueByStyleFormat(xfIndex, cellValue);
                                                SetCellsValueAndHeaders(cellValue, useHeaderRow, ref headRows, ref isFirstRow, ref cell, columnIndex);
                                            }
                                            else
                                            {
                                                SetCellsValueAndHeaders(cellValue, useHeaderRow, ref headRows, ref isFirstRow, ref cell, columnIndex);
                                            }
                                        }
                                        else if (!XmlReaderHelper.SkipContent(reader))
                                            break;
                                    }

                                    if (isFirstRow)
                                    {
                                        isFirstRow = false; // for startcell logic
                                        if (useHeaderRow)
                                            continue;
                                    }


                                    yield return cell;
                                }
                            }
                            else if (!XmlReaderHelper.SkipContent(reader))
                            {
                                break;
                            }
                        }

                    }
                    else if (!XmlReaderHelper.SkipContent(reader))
                    {
                        break;
                    }
                }
            }
        }

        private IDictionary<string, object> GetCell(bool useHeaderRow, int maxColumnIndex, Dictionary<int, string> headRows, int startColumnIndex)
        {
            return useHeaderRow ? CustomPropertyHelper.GetEmptyExpandoObject(headRows) : CustomPropertyHelper.GetEmptyExpandoObject(maxColumnIndex, startColumnIndex);
        }

        private void SetCellsValueAndHeaders(object cellValue, bool useHeaderRow, ref Dictionary<int, string> headRows, ref bool isFirstRow, ref IDictionary<string, object> cell, int columnIndex)
        {
            if (useHeaderRow)
            {
                if (isFirstRow) // for startcell logic
                {
                    var cellValueString = cellValue?.ToString();
                    if (!string.IsNullOrWhiteSpace(cellValueString))
                        headRows.Add(columnIndex, cellValueString);
                }
                else
                {
                    if (headRows.ContainsKey(columnIndex))
                    {
                        var key = headRows[columnIndex];
                        cell[key] = cellValue;
                    }
                }
            }
            else
            {
                //if not using First Head then using A,B,C as index
                cell[ColumnHelper.GetAlphabetColumnName(columnIndex)] = cellValue;
            }
        }

        public IEnumerable<T> Query<T>(string sheetName, string startCell) where T : class, new()
        {
            return ExcelOpenXmlSheetReader.QueryImpl<T>(Query(false, sheetName, startCell), startCell, this._config);
        }

        public static IEnumerable<T> QueryImpl<T>(IEnumerable<IDictionary<string, object>> values, string startCell, Configuration configuration) where T : class, new()
        {
            var type = typeof(T);

            List<ExcelCustomPropertyInfo> props = null;
            //TODO:need to optimize

            string[] headers = null;

            Dictionary<string, int> headersDic = null;
            string[] keys = null;
            var first = true;
            var rowIndex = 0;
            foreach (var item in values)
            {

                if (first)
                {
                    keys = item.Keys.ToArray();//.Select((s, i) => new { s,i}).ToDictionary(_=>_.s,_=>_.i);
                    headers = item?.Values?.Select(s => s?.ToString())?.ToArray(); //TODO:remove
                    headersDic = headers.Select((o, i) => new { o=(o == null ? "" : o), i } )
                        .OrderBy(x => x.i)
                        .GroupBy(x => x.o)
                        .Select(group => new { Group = group, Count = group.Count() })
                        .SelectMany(groupWithCount =>
                           groupWithCount.Group.Select(b => b)
                           .Zip(
                               Enumerable.Range(1, groupWithCount.Count),
                               (j, i) => new { key = (i == 1 ? j.o : $"{j.o}_____{i}"), idx = j.i, RowNumber = i }
                           )
                        ).ToDictionary(_ => _.key, _ => _.idx);
                    //TODO: alert don't duplicate column name
                    props = CustomPropertyHelper.GetExcelCustomPropertyInfos(type, keys);
                    first = false;
                    continue;
                }
                var v = new T();
                foreach (var pInfo in props)
                {
                    if (pInfo.ExcelColumnAliases != null)
                    {
                        foreach (var alias in pInfo.ExcelColumnAliases)
                        {
                            if (headersDic.ContainsKey(alias))
                            {
                                object newV = null;
                                object itemValue = item[keys[headersDic[alias]]];

                                if (itemValue == null)
                                    continue;

                                newV = TypeHelper.TypeMapping(v, pInfo, newV, itemValue, rowIndex, startCell, configuration);
                            }
                        }
                    }


                    //Q: Why need to check every time? A: it needs to check everytime, because it's dictionary
                    {
                        object newV = null;
                        object itemValue = null;
                        if (pInfo.ExcelXName != null && keys.Contains(pInfo.ExcelXName))
                            itemValue = item[pInfo.ExcelXName];
                        else if (headersDic.ContainsKey(pInfo.ExcelColumnName))
                            itemValue = item[keys[headersDic[pInfo.ExcelColumnName]]];

                        if (itemValue == null)
                            continue;

                        newV = TypeHelper.TypeMapping(v, pInfo, newV, itemValue, rowIndex, startCell, configuration);
                    }
                }
                rowIndex++;
                yield return v;
            }
        }

        private void SetSharedStrings()
        {
            if (_sharedStrings != null)
                return;
            var sharedStringsEntry = _archive.GetEntry("xl/sharedStrings.xml");
            if (sharedStringsEntry == null)
                return;
            using (var stream = sharedStringsEntry.Open())
            {
                if (_config.EnableSharedStringCache && sharedStringsEntry.Length >= _config.SharedStringCacheSize)
                {
                    // use sqlite
                    var dbList = new DbList(Guid.NewGuid().ToString());

                    var list = new List<string>();
                    foreach (var sharedString in XmlReaderHelper.GetSharedStrings(stream, _ns))
                    {
                        list.Add(sharedString);
                        if (list.Count >= 10000)
                        {
                            dbList.AddRange(list);
                            list.Clear();
                        }
                    }

                    if (list.Count > 0)
                    {
                        dbList.AddRange(list);
                        list.Clear();
                    }
                    
                    _sharedStrings = dbList;
                }
                else
                {
                    _sharedStrings = XmlReaderHelper.GetSharedStrings(stream, _ns).ToList();
                }
            }  
        }

        internal IList<string> GetSharedStrings()
        {
            if (_sharedStrings == null)
                SetSharedStrings();
            return _sharedStrings;
        }

        private void SetWorkbookRels(ReadOnlyCollection<ZipArchiveEntry> entries)
        {
            if (_sheetRecords != null)
                return;
            _sheetRecords = GetWorkbookRels(entries);
        }

        internal IEnumerable<SheetRecord> ReadWorkbook(ReadOnlyCollection<ZipArchiveEntry> entries)
        {
            using (var stream = entries.Single(w => w.FullName == "xl/workbook.xml").Open())
            using (XmlReader reader = XmlReader.Create(stream, _xmlSettings))
            {
                if (!XmlReaderHelper.IsStartElement(reader, "workbook", _ns))
                    yield break;

                if (!XmlReaderHelper.ReadFirstContent(reader))
                    yield break;

                while (!reader.EOF)
                {
                    if (XmlReaderHelper.IsStartElement(reader, "sheets", _ns))
                    {
                        if (!XmlReaderHelper.ReadFirstContent(reader))
                            continue;

                        while (!reader.EOF)
                        {
                            if (XmlReaderHelper.IsStartElement(reader, "sheet", _ns))
                            {
                                yield return new SheetRecord(
                                    reader.GetAttribute("name"),
                                    uint.Parse(reader.GetAttribute("sheetId")),
                                    XmlReaderHelper.GetAttribute(reader, "id", _relationshiopNs)
                                );
                                reader.Skip();
                            }
                            else if (!XmlReaderHelper.SkipContent(reader))
                            {
                                break;
                            }
                        }
                    }
                    else if (!XmlReaderHelper.SkipContent(reader))
                    {
                        yield break;
                    }
                }
            }
        }

        internal List<SheetRecord> GetWorkbookRels(ReadOnlyCollection<ZipArchiveEntry> entries)
        {
            var sheetRecords = ReadWorkbook(entries).ToList();

            using (var stream = entries.Single(w => w.FullName == "xl/_rels/workbook.xml.rels").Open())
            using (XmlReader reader = XmlReader.Create(stream, _xmlSettings))
            {
                if (!XmlReaderHelper.IsStartElement(reader, "Relationships", "http://schemas.openxmlformats.org/package/2006/relationships"))
                    return null;

                if (!XmlReaderHelper.ReadFirstContent(reader))
                    return null;

                while (!reader.EOF)
                {
                    if (XmlReaderHelper.IsStartElement(reader, "Relationship", "http://schemas.openxmlformats.org/package/2006/relationships"))
                    {
                        string rid = reader.GetAttribute("Id");
                        foreach (var sheet in sheetRecords)
                        {
                            if (sheet.Rid == rid)
                            {
                                sheet.Path = reader.GetAttribute("Target");
                                break;
                            }
                        }

                        reader.Skip();
                    }
                    else if (!XmlReaderHelper.SkipContent(reader))
                    {
                        break;
                    }
                }
            }

            return sheetRecords;
        }

        private object ReadCellAndSetColumnIndex(XmlReader reader, ref int columnIndex, bool withoutCR, int startColumnIndex, string aR, string aT)
        {
            var newColumnIndex = 0;
            int xfIndex = -1;

            if (withoutCR)
                newColumnIndex = columnIndex + 1;
            //TODO:need to check only need nextColumnIndex or columnIndex
            else if (ReferenceHelper.ParseReference(aR, out int referenceColumn, out _))
                newColumnIndex = referenceColumn - 1; // ParseReference is 1-based
            else
                newColumnIndex = columnIndex;

            columnIndex = newColumnIndex;

            if (columnIndex < startColumnIndex)
            {
                if (!XmlReaderHelper.ReadFirstContent(reader))
                    return null;
                while (!reader.EOF)
                    if (!XmlReaderHelper.SkipContent(reader))
                        break;
                return null;
            }

            if (!XmlReaderHelper.ReadFirstContent(reader))
                return null;

            object value = null;
            while (!reader.EOF)
            {
                if (XmlReaderHelper.IsStartElement(reader, "v", _ns))
                {
                    string rawValue = reader.ReadElementContentAsString();
                    if (!string.IsNullOrEmpty(rawValue))
                        ConvertCellValue(rawValue, aT, xfIndex, out value);
                }
                else if (XmlReaderHelper.IsStartElement(reader, "is", _ns))
                {
                    string rawValue = StringHelper.ReadStringItem(reader);
                    if (!string.IsNullOrEmpty(rawValue))
                        ConvertCellValue(rawValue, aT, xfIndex, out value);
                }
                else if (!XmlReaderHelper.SkipContent(reader))
                {
                    break;
                }
            }



            return value;
        }

        private void ConvertCellValue(string rawValue, string aT, int xfIndex, out object value)
        {
            const NumberStyles style = NumberStyles.Any;
            var invariantCulture = CultureInfo.InvariantCulture;

            switch (aT)
            {
                case "s":
                    if (int.TryParse(rawValue, style, invariantCulture, out var sstIndex))
                    {
                        if (sstIndex >= 0 && sstIndex < _sharedStrings.Count)
                        {
                            //value = Helpers.ConvertEscapeChars(_SharedStrings[sstIndex]);
                            value = XmlEncoder.DecodeString(_sharedStrings[sstIndex]);
                            return;
                        }
                    }
                    value = null;
                    return;
                case "inlineStr":
                case "str":
                    //TODO: it will unbox,box
                    var v = XmlEncoder.DecodeString(rawValue);
                    if (_config.EnableConvertByteArray)
                    {
                        //TODO:optimize startswith
                        //if str start with "data:image/png;base64," then convert to byte[] https://github.com/shps951023/MiniExcel/issues/318
                        if (v != null && v.StartsWith("@@@fileid@@@,"))
                        {
                            var path = v.Substring(13);
                            var stream = _archive.GetEntry(path).Open();
                            byte[] bytes;
                            using (var ms = new MemoryStream())
                            {
                                stream.CopyTo(ms);
                                bytes = ms.ToArray();
                            }
                            value = bytes;
                        }
                        else
                        {
                            value = v;
                        } 
                    }
                    else
                    {
                        value = v;
                    }
                    return;
                case "b":
                    value = rawValue == "1";
                    return;
                case "d":
                    if (DateTime.TryParseExact(rawValue, "yyyy-MM-dd", invariantCulture, DateTimeStyles.AllowLeadingWhite | DateTimeStyles.AllowTrailingWhite, out var date))
                    {
                        value = date;
                        return;
                    }

                    value = rawValue;
                    return;
                case "e":
                    value = rawValue;
                    return;
                default:
                    if (double.TryParse(rawValue, style, invariantCulture, out var n))
                    {
                        value = n;
                        return;
                    }

                    value = rawValue;
                    return;
            }
        }

        public Task<IEnumerable<IDictionary<string, object>>> QueryAsync(bool UseHeaderRow, string sheetName, string startCell)
        {
            return Task.Run(() => Query(UseHeaderRow, sheetName, startCell));
        }

        public Task<IEnumerable<T>> QueryAsync<T>(string sheetName, string startCell) where T : class, new()
        {
            return Task.Run(() => Query<T>(sheetName, startCell));
        }
    }
}
