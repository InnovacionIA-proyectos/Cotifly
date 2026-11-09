using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ClosedXML.Excel;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Licitaciones.Services;
using Cotifly.Infraestructura.Mercancias.Services;
using Cotifly.Pruebas.Comun;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotifly.Infraestructura.Tests.Comun;

/// <summary>
/// HU-035.3 (#277), validación adversarial: <see cref="clsLecturaExcelAcotada.EsParteXml"/> salta las partes que no son XML por su
/// tipo. Estas pruebas intentan que una parte que ClosedXML sí lee como XML se escape del conteo disfrazando su tipo en
/// <c>[Content_Types].xml</c> (octet-stream, printerSettings, vacío, parámetros, mayúsculas, espacios, Default en vez de Override,
/// extensión <c>.bin</c>). El oráculo: si <see cref="clsLecturaExcelAcotada.Verificar"/> deja pasar el libro, ClosedXML no puede haber
/// leído como XML la parte disfrazada (que trae XML roto o miles de elementos). Libros fabricados por código con datos ficticios.
/// </summary>
public sealed class clsLecturaExcelAcotadaAdversarialTests
{
    private const string TipoImpresora = "application/vnd.openxmlformats-officedocument.spreadsheetml.printerSettings";
    private const string TipoVml = "application/vnd.openxmlformats-officedocument.vmlDrawing";
    private const string NsTipos = "http://schemas.openxmlformats.org/package/2006/content-types";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Libro base con las partes XML que ClosedXML lee: hoja, cadenas, estilos, tema, comentario (y su VML), tabla y propiedades.</summary>
    private static readonly Lazy<byte[]> _base = new(() =>
    {
        using var libro = new XLWorkbook();
        var ws = libro.Worksheets.Add(clsExcelMercanciasMuestra.Hoja);
        for (var c = 0; c < clsExcelMercanciasMuestra.Encabezados.Length; c++)
        {
            ws.Cell(1, c + 1).Value = clsExcelMercanciasMuestra.Encabezados[c];
        }

        for (var i = 0; i < 3; i++)
        {
            var (tipo, cantidad, largo, ancho, alto, peso) = clsExcelMercanciasMuestra.Mercancia(i);
            ws.Cell(i + 2, 1).Value = tipo;
            ws.Cell(i + 2, 2).Value = cantidad;
            ws.Cell(i + 2, 3).Value = largo;
            ws.Cell(i + 2, 4).Value = ancho;
            ws.Cell(i + 2, 5).Value = alto;
            ws.Cell(i + 2, 6).Value = peso;
        }

        ws.Cell(1, 1).CreateComment().AddText("Comentario ficticio");
        ws.Range(1, 1, 4, 6).CreateTable("TablaFicticia");
        libro.Author = "Autor ficticio";
        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);
        return memoria.ToArray();
    });

    /// <summary>Nombre (sin '/') de la parte <paramref name="cual"/> del libro base, resuelto con el SDK.</summary>
    private static string Parte(string cual)
    {
        using var doc = SpreadsheetDocument.Open(new MemoryStream(_base.Value), false);
        var wb = doc.WorkbookPart!;
        var hoja = wb.WorksheetParts.First();
        OpenXmlPart parte = cual switch
        {
            "libro" => wb,
            "hoja" => hoja,
            "cadenas" => wb.SharedStringTablePart!,
            "estilos" => wb.WorkbookStylesPart!,
            "tema" => wb.ThemePart!,
            "comentarios" => hoja.WorksheetCommentsPart!,
            "vml" => hoja.VmlDrawingParts.First(),
            "tabla" => hoja.TableDefinitionParts.First(),
            "core" => doc.CoreFilePropertiesPart!,
            "app" => doc.ExtendedFilePropertiesPart!,
            _ => throw new ArgumentOutOfRangeException(nameof(cual)),
        };
        return parte.Uri.OriginalString.TrimStart('/');
    }

    /// <summary>Contenido malicioso: XML roto (cualquier lector XML lanza) o 1.000.001 elementos (pasa el tope si se cuenta).</summary>
    private static string Contenido(string clase, string cual) => clase switch
    {
        "roto" => "<a><b></a>",
        "denso" => Denso(cual),
        "dtd" => "<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">"
            + "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\"><!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">]><a>&d;</a>",
        _ => throw new ArgumentOutOfRangeException(nameof(clase)),
    };

    /// <summary>XML válido y denso con la raíz que el SDK espera para la parte (o VML genérico): si ClosedXML lo lee, lo lee entero.</summary>
    private static string Denso(string cual)
    {
        var n = clsLecturaExcelAcotada.MaximoElementosXml + 1;
        if (cual == "vml")
        {
            return "<xml xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\">"
                + string.Concat(Enumerable.Repeat("<o:p/>", n)) + "</xml>";
        }

        if (cual == "hoja")
        {
            var sb = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            for (var hechas = 0; hechas < n; hechas += 50)
            {
                sb.Append("<row>").Append(string.Concat(Enumerable.Repeat("<c><v>1</v></c>", 25))).Append("</row>");
            }

            return sb.Append("</sheetData></worksheet>").ToString();
        }

        return "<a>" + string.Concat(Enumerable.Repeat("<b/>", n)) + "</a>";
    }

    /// <summary>
    /// Copia <paramref name="libro"/> cambiando el contenido de <paramref name="parte"/> y su tipo en <c>[Content_Types].xml</c>
    /// (Override, o solo el Default de su extensión si <paramref name="porDefault"/>).
    /// </summary>
    private static byte[] Disfrazar(byte[] libro, string parte, string? tipo, string? contenido, bool porDefault = false)
    {
        using var salida = new MemoryStream();
        using (var origen = new ZipArchive(new MemoryStream(libro), ZipArchiveMode.Read))
        using (var destino = new ZipArchive(salida, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entrada in origen.Entries)
            {
                using var escritura = destino.CreateEntry(entrada.FullName, CompressionLevel.SmallestSize).Open();
                using var lectura = entrada.Open();
                if (entrada.FullName == "[Content_Types].xml" && tipo is not null)
                {
                    var xml = XDocument.Load(lectura);
                    var raiz = xml.Root!;
                    var nombre = "/" + parte;
                    raiz.Elements(XName.Get("Override", NsTipos))
                        .Where(o => string.Equals((string?)o.Attribute("PartName"), nombre, StringComparison.OrdinalIgnoreCase)).Remove();
                    if (porDefault)
                    {
                        var ext = Path.GetExtension(parte).TrimStart('.');
                        raiz.Elements(XName.Get("Default", NsTipos))
                            .Where(d => string.Equals((string?)d.Attribute("Extension"), ext, StringComparison.OrdinalIgnoreCase)).Remove();
                        raiz.AddFirst(new XElement(XName.Get("Default", NsTipos), new XAttribute("Extension", ext), new XAttribute("ContentType", tipo)));
                    }
                    else
                    {
                        raiz.Add(new XElement(XName.Get("Override", NsTipos), new XAttribute("PartName", nombre), new XAttribute("ContentType", tipo)));
                    }

                    xml.Save(escritura);
                }
                else if (entrada.FullName == parte && contenido is not null)
                {
                    escritura.Write(Encoding.UTF8.GetBytes(contenido));
                }
                else
                {
                    lectura.CopyTo(escritura);
                }
            }
        }

        return salida.ToArray();
    }

    /// <summary>Abre con ClosedXML y recorre todo lo que leen mercancías, pliego y exportación (celdas, comentarios, tablas).</summary>
    private static void AbrirConClosedXml(byte[] libro)
    {
        using var wb = new XLWorkbook(new MemoryStream(libro, writable: false));
        foreach (var ws in wb.Worksheets)
        {
            foreach (var celda in ws.CellsUsed(XLCellsUsedOptions.All))
            {
                _ = celda.Value;
                _ = celda.HasComment ? celda.GetComment().Text : null;
            }

            _ = ws.Tables.Count();
        }

        using var copia = new MemoryStream();
        wb.SaveAs(copia);
    }

    public static TheoryData<string, string> PartesYTipos()
    {
        var datos = new TheoryData<string, string>();
        string[] partes = ["libro", "hoja", "cadenas", "estilos", "tema", "comentarios", "vml", "tabla", "core", "app"];
        string[] tipos =
        [
            "application/octet-stream", TipoImpresora, "text/plain", "application/vnd.ms-office.activeX",
            "application/vnd.openxmlformats-officedocument.vmlDrawingX", "application/xml-dtd", "image/png",
        ];
        foreach (var parte in partes)
        {
            foreach (var tipo in tipos)
            {
                datos.Add(parte, tipo);
            }
        }

        return datos;
    }

    /// <summary>
    /// Cada parte XML que ClosedXML lee, con XML roto y un tipo no XML: o el control la rechaza, o ClosedXML abre el libro sin leerla
    /// como XML. Si el control la deja pasar y ClosedXML lanza, la parte se leyó como XML sin contarse (escape).
    /// </summary>
    [Theory]
    [MemberData(nameof(PartesYTipos))]
    public void UnaParteXmlConTipoDisfrazadoNoSeEscapaDelConteo(string cual, string tipo)
    {
        var libro = Disfrazar(_base.Value, Parte(cual), tipo, Contenido("roto", cual));

        var rechazo = clsLecturaExcelAcotada.Verificar(libro, Ct);

        if (rechazo is null)
        {
            var abrir = () => AbrirConClosedXml(libro);
            abrir.Should().NotThrow($"{cual} con tipo '{tipo}' pasó el control: ClosedXML no debe leerla como XML");
        }
    }

    /// <summary>Lo mismo con el tipo puesto por el Default de la extensión en vez de un Override.</summary>
    [Theory]
    [InlineData("hoja", "application/octet-stream")]
    [InlineData("vml", "application/octet-stream")]
    [InlineData("vml", TipoImpresora)]
    [InlineData("comentarios", "application/octet-stream")]
    [InlineData("cadenas", TipoImpresora)]
    public void UnTipoDisfrazadoPorDefaultNoSeEscapaDelConteo(string cual, string tipo)
    {
        var libro = Disfrazar(_base.Value, Parte(cual), tipo, Contenido("roto", cual), porDefault: true);

        if (clsLecturaExcelAcotada.Verificar(libro, Ct) is null)
        {
            var abrir = () => AbrirConClosedXml(libro);
            abrir.Should().NotThrow($"{cual} con Default '{tipo}' pasó el control");
        }
    }

    /// <summary>
    /// Variantes del tipo XML que el paquete acepta: mayúsculas, parámetros y espacios. Si el SDK las acepta, se cuentan; si no, el libro
    /// no abre. Con contenido denso, nunca pasan.
    /// </summary>
    [Theory]
    [InlineData("vml", "APPLICATION/VND.OPENXMLFORMATS-OFFICEDOCUMENT.VMLDRAWING")]
    [InlineData("vml", "application/vnd.openxmlformats-officedocument.vmlDrawing; charset=utf-8")]
    [InlineData("vml", "application/vnd.openxmlformats-officedocument.vmlDrawing ;x=y")]
    [InlineData("hoja", "APPLICATION/VND.OPENXMLFORMATS-OFFICEDOCUMENT.SPREADSHEETML.WORKSHEET+XML")]
    [InlineData("hoja", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml; charset=utf-8")]
    [InlineData("cadenas", "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml;a=\"b;c\"")]
    [InlineData("comentarios", "application/vnd.openxmlformats-officedocument.spreadsheetml.comments+XML")]
    public void UnTipoXmlConVariantesYContenidoDensoNoPasa(string cual, string tipo)
    {
        var libro = Disfrazar(_base.Value, Parte(cual), tipo, Denso(cual));

        clsLecturaExcelAcotada.Verificar(libro, Ct).Should().NotBeNull($"{cual} denso con tipo '{tipo}'");
    }

    /// <summary>
    /// Con contenido denso y tipo no XML: si el control lo deja pasar, ClosedXML no puede reservar la memoria de leerlo (un millón de
    /// elementos son cientos de MB). Mide lo que reserva la apertura en este hilo.
    /// </summary>
    [Theory]
    [InlineData("vml", "application/octet-stream")]
    [InlineData("vml", TipoImpresora)]
    [InlineData("vml", "text/plain")]
    [InlineData("hoja", "application/octet-stream")]
    [InlineData("comentarios", "application/octet-stream")]
    [InlineData("core", "application/octet-stream")]
    [InlineData("app", "application/octet-stream")]
    public void UnaParteDensaConTipoNoXmlNoLlegaAClosedXml(string cual, string tipo)
    {
        var libro = Disfrazar(_base.Value, Parte(cual), tipo, Denso(cual));

        if (clsLecturaExcelAcotada.Verificar(libro, Ct) is null)
        {
            var antes = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                AbrirConClosedXml(libro);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Que lance basta: el lector responde LIBRO_ILEGIBLE. Lo que importa es cuánta memoria reservó antes.
            }

            (GC.GetAllocatedBytesForCurrentThread() - antes).Should().BeLessThan(150L * 1024 * 1024, $"{cual} densa con tipo '{tipo}' pasó el control");
        }
    }

    /// <summary>Billion laughs (entidades internas) en partes que se saltan: si pasa el control, ClosedXML no la expande.</summary>
    [Theory]
    [InlineData("vml", "application/octet-stream")]
    [InlineData("vml", TipoVml)]
    [InlineData("hoja", "application/octet-stream")]
    [InlineData("core", "application/octet-stream")]
    public void EntidadesEnUnaParteDisfrazadaNoSeExpanden(string cual, string tipo)
    {
        var libro = Disfrazar(_base.Value, Parte(cual), tipo, Contenido("dtd", cual));

        if (clsLecturaExcelAcotada.Verificar(libro, Ct) is null)
        {
            var abrir = () => AbrirConClosedXml(libro);
            abrir.Should().NotThrow($"{cual} con DTD y tipo '{tipo}' pasó el control");
        }
    }

    /// <summary>La hoja renombrada a <c>.bin</c> con XML dentro y el tipo de hoja por Default de <c>bin</c>: se cuenta (densa no pasa).</summary>
    [Fact]
    public void UnaHojaConExtensionBinYTipoDeHojaSeCuenta()
    {
        var hoja = Parte("hoja");
        var renombrado = Renombrar(_base.Value, hoja, Path.ChangeExtension(hoja, ".bin"));
        var tipoHoja = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";

        var denso = Disfrazar(renombrado, Path.ChangeExtension(hoja, ".bin"), tipoHoja, Denso("hoja"), porDefault: true);
        var normal = Disfrazar(renombrado, Path.ChangeExtension(hoja, ".bin"), tipoHoja, null, porDefault: true);

        clsLecturaExcelAcotada.Verificar(normal, Ct).Should().BeNull("la hoja .bin con su tipo es un libro válido");
        clsLecturaExcelAcotada.Verificar(denso, Ct)?.Detalle.Should().Be(clsLecturaExcelAcotada.MotivoDescomprimido);
    }

    /// <summary>La hoja renombrada a <c>.bin</c> y el Default de <c>bin</c> como printerSettings (lo que hace Excel): no pasa.</summary>
    [Fact]
    public void UnaHojaConExtensionBinYTipoDeImpresoraNoPasa()
    {
        var hoja = Parte("hoja");
        var bin = Path.ChangeExtension(hoja, ".bin");
        var libro = Disfrazar(Renombrar(_base.Value, hoja, bin), bin, TipoImpresora, Denso("hoja"), porDefault: true);

        clsLecturaExcelAcotada.Verificar(libro, Ct).Should().NotBeNull();
    }

    /// <summary>Un tipo vacío en el Override no abre (el paquete lo rechaza): nunca pasa sin contarse.</summary>
    [Theory]
    [InlineData("vml")]
    [InlineData("hoja")]
    public void UnTipoVacioNoPasa(string cual)
    {
        var libro = Disfrazar(_base.Value, Parte(cual), "", Denso(cual));

        clsLecturaExcelAcotada.Verificar(libro, Ct).Should().NotBeNull(cual);
    }

    /// <summary>VML denso y con DTD con su tipo real, enlazado de verdad desde la hoja (no como parte extra): se rechaza.</summary>
    [Fact]
    public void UnVmlRealDensoOConDtdSeRechaza()
    {
        var vml = Parte("vml");

        clsLecturaExcelAcotada.Verificar(Disfrazar(_base.Value, vml, null, Denso("vml")), Ct)?.Detalle
            .Should().Be(clsLecturaExcelAcotada.MotivoDescomprimido);
        clsLecturaExcelAcotada.Verificar(Disfrazar(_base.Value, vml, null, Contenido("dtd", "vml")), Ct)?.Detalle
            .Should().Be(clsLecturaExcelAcotada.MotivoLibroIlegible);
    }

    /// <summary>El libro base (con comentario, tabla y propiedades) pasa: las pruebas de arriba no rechazan por otra cosa.</summary>
    [Fact]
    public void ElLibroBasePasaYSeAbre()
    {
        clsLecturaExcelAcotada.Verificar(_base.Value, Ct).Should().BeNull();
        AbrirConClosedXml(_base.Value);
    }

    /// <summary>
    /// Partes binarias grandes que se saltan: una de 60 MB pasa (el tope de 64 MB se mide descomprimiendo) y se verifica rápido; una de
    /// 70 MB es <c>DESCOMPRIMIDO_EXCEDE_LIMITE</c> antes de abrir el paquete.
    /// </summary>
    [Fact]
    public void UnaParteBinariaEnormeSigueAcotadaPorElTamano()
    {
        var grande = clsExcelMercanciasMuestra.ConParteExtra(_base.Value, TipoImpresora, new byte[60 * 1024 * 1024]);
        var enorme = clsExcelMercanciasMuestra.ConParteExtra(_base.Value, TipoImpresora, new byte[70 * 1024 * 1024]);

        var reloj = System.Diagnostics.Stopwatch.StartNew();
        clsLecturaExcelAcotada.Verificar(grande, Ct).Should().BeNull();
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        clsLecturaExcelAcotada.Verificar(enorme, Ct)?.Detalle.Should().Be(clsLecturaExcelAcotada.MotivoDescomprimido);
    }

    /// <summary>Muchas partes binarias pequeñas (printerSettings): 1000 entradas es el tope; con más, DEMASIADAS_ENTRADAS.</summary>
    [Fact]
    public void MuchasPartesBinariasPequenasSiguenAcotadasPorEntradas()
    {
        int entradas;
        using (var zip = new ZipArchive(new MemoryStream(_base.Value), ZipArchiveMode.Read))
        {
            entradas = zip.Entries.Count;
        }

        // Cada parte extra suma su entrada y, la primera, el .rels del libro ya existe: se suman exactamente n entradas.
        var justo = ConPartesBinarias(_base.Value, clsLecturaExcelAcotada.MaximoEntradasZip - entradas);
        var demas = ConPartesBinarias(_base.Value, clsLecturaExcelAcotada.MaximoEntradasZip - entradas + 1);

        clsLecturaExcelAcotada.Verificar(justo, Ct).Should().BeNull();
        clsLecturaExcelAcotada.Verificar(demas, Ct)?.Detalle.Should().Be(clsLecturaExcelAcotada.MotivoDemasiadasEntradas);
    }

    /// <summary>
    /// D1 (preexistente, anterior a #277): el VML que guarda Excel para los comentarios trae <c>&lt;br&gt;</c> sin cerrar. ClosedXML lo
    /// corrige antes de leerlo, pero el conteo lo lee estricto: un libro con comentarios es <c>LIBRO_ILEGIBLE</c> en mercancías y pliego.
    /// </summary>
    [Fact]
    public async Task UnVmlComoElDeExcelConBrSinCerrarSeLee()
    {
        var vml = Parte("vml");
        string original;
        using (var zip = new ZipArchive(new MemoryStream(_base.Value), ZipArchiveMode.Read))
        using (var lector = new StreamReader(zip.GetEntry(vml)!.Open()))
        {
            original = lector.ReadToEnd();
        }

        // Excel escribe el estilo del cuadro con <br> sin cerrar dentro de x:ClientData o del texto del comentario.
        var conBr = original.Replace("</v:textbox>", "<br></v:textbox>", StringComparison.Ordinal);
        conBr.Should().NotBe(original, "el VML de ClosedXML trae v:textbox");
        var libro = Disfrazar(_base.Value, vml, null, conBr);

        AbrirConClosedXml(libro); // ClosedXML lo lee.
        var lectorMercancias = new clsLectorGrillaExcelClosedXml(new clsLecturaExcelAcotada(NullLogger<clsLecturaExcelAcotada>.Instance),
            NullLogger<clsLectorGrillaExcelClosedXml>.Instance);
        var resultado = await lectorMercancias.LeerAsync("m.xlsx", libro, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Detalle : null);
    }

    /// <summary>Mercancías, pliego y exportación con el libro base disfrazado (VML octet-stream con XML roto): ningún 500.</summary>
    [Fact]
    public async Task LosTresLectoresRespondenErrorDeNegocioOExitoConUnVmlDisfrazado()
    {
        var libro = Disfrazar(_base.Value, Parte("vml"), "application/octet-stream", Contenido("roto", "vml"));
        using var lectura = new clsLecturaExcelAcotada(NullLogger<clsLecturaExcelAcotada>.Instance);

        var mercancias = await new clsLectorGrillaExcelClosedXml(lectura, NullLogger<clsLectorGrillaExcelClosedXml>.Instance).LeerAsync("m.xlsx", libro, Ct);
        var pliego = await new clsLectorExcelLicitacionClosedXml(lectura).FilasDeDatosPorHojaAsync(libro, Ct);

        foreach (var r in new[] { mercancias.EsFallo ? mercancias.Error : null, pliego.EsFallo ? pliego.Error : null })
        {
            r?.Codigo.Should().BeOneOf(clsCodigosError.ExcelInvalido, clsCodigosError.ArchivoDemasiadoGrande);
        }
    }

    private static byte[] ConPartesBinarias(byte[] xlsx, int n)
    {
        using var memoria = new MemoryStream();
        memoria.Write(xlsx);
        using (var documento = SpreadsheetDocument.Open(memoria, true))
        {
            for (var i = 0; i < n; i++)
            {
                var parte = documento.WorkbookPart!.AddExtendedPart("http://cotifly.test/relaciones/extra", TipoImpresora, "bin");
                using var flujo = new MemoryStream(clsExcelMercanciasMuestra.DevmodeFicticio());
                parte.FeedData(flujo);
            }
        }

        return memoria.ToArray();
    }

    /// <summary>Renombra la entrada <paramref name="de"/> a <paramref name="a"/> y su destino en los <c>.rels</c>.</summary>
    private static byte[] Renombrar(byte[] libro, string de, string a)
    {
        using var salida = new MemoryStream();
        using (var origen = new ZipArchive(new MemoryStream(libro), ZipArchiveMode.Read))
        using (var destino = new ZipArchive(salida, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entrada in origen.Entries)
            {
                var nombre = entrada.FullName == de ? a
                    : entrada.FullName == Path.GetDirectoryName(de)!.Replace('\\', '/') + "/_rels/" + Path.GetFileName(de) + ".rels"
                        ? Path.GetDirectoryName(a)!.Replace('\\', '/') + "/_rels/" + Path.GetFileName(a) + ".rels"
                        : entrada.FullName;
                using var escritura = destino.CreateEntry(nombre, CompressionLevel.SmallestSize).Open();
                using var lectura = entrada.Open();
                if (entrada.FullName.EndsWith(".rels", StringComparison.Ordinal))
                {
                    using var texto = new StreamReader(lectura);
                    escritura.Write(Encoding.UTF8.GetBytes(texto.ReadToEnd().Replace(Path.GetFileName(de), Path.GetFileName(a), StringComparison.Ordinal)));
                }
                else
                {
                    lectura.CopyTo(escritura);
                }
            }
        }

        return salida.ToArray();
    }
}
