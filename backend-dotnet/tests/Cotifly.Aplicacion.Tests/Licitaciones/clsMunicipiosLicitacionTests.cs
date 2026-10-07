using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>
/// HU-013.11 (#108, G6): el origen y el destino de cada fila de una licitación se resuelven a ids de municipio de Nexo antes de
/// guardar, con la regla determinista de HU-005.13. Una sola coincidencia se usa; varias o ninguna son un error de la fila (nunca
/// se adivina).
/// </summary>
public sealed class clsMunicipiosLicitacionTests
{
    private static readonly IReadOnlyList<clsMunicipio> _catalogo =
    [
        new(76109, "76109", "Buenaventura", "Valle del Cauca", "Colombia"),
        new(11001, "11001", "Bogotá, D.C.", "Bogotá, D.C.", "Colombia"),
        new(76001, "76001", "Cali", "Valle del Cauca", "Colombia"),
        new(5400, "05400", "La Unión", "Antioquia", "Colombia"),
        new(52399, "52399", "La Unión", "Nariño", "Colombia"),
        new(76400, "76400", "La Unión", "Valle del Cauca", "Colombia"),
    ];

    private static clsLineaLicitacionEntrada Fila(int fila, string origen, string destino, int? origenId = null, int? destinoId = null) =>
        new(fila, new clsLineaSolicitudCosteo { Origen = origen, Destino = destino, OrigenMunicipioId = origenId, DestinoMunicipioId = destinoId, PesoKg = 1000m });

    [Fact]
    public void UnaSolaCoincidenciaDaElIdSinCambiarElTexto()
    {
        var (lineas, errores) = clsMunicipiosLicitacion.Resolver([Fila(0, " BUENAVENTURA ", "BOGOTA, D.C.")], _catalogo);

        errores.Should().BeEmpty();
        var linea = lineas[0]!.Linea;
        (linea.OrigenMunicipioId, linea.DestinoMunicipioId).Should().Be(((int?)76109, (int?)11001));
        linea.Origen.Should().Be(" BUENAVENTURA ", "el texto se guarda recortado al persistir, no se reemplaza por el del catálogo");
    }

    [Fact]
    public void ConDepartamentoUnNombreRepetidoSeResuelve() =>
        clsMunicipiosLicitacion.Resolver([Fila(0, "La Unión, Nariño", "Cali")], _catalogo).Lineas[0]!.Linea.OrigenMunicipioId.Should().Be(52399);

    [Fact]
    public void UnNombreRepetidoSinDepartamentoEsAmbiguoConLosCandidatos()
    {
        var (_, errores) = clsMunicipiosLicitacion.Resolver([Fila(0, "Cali", "Cali"), Fila(4, "La Unión", "Cali")], _catalogo);

        var error = errores.Should().ContainSingle().Subject;
        (error.Indice, error.Fila, error.Campo, error.Codigo).Should().Be(((int?)1, (int?)4, "origen", clsCodigosLineaSolicitud.MunicipioAmbiguo));
        error.Mensaje.Should().Contain("La Unión, Antioquia (id 5400)").And.Contain("La Unión, Nariño (id 52399)").And.Contain("origen_municipio_id");
    }

    [Theory]
    [InlineData("Macondo")]
    [InlineData("Bue")]
    public void SinCoincidenciaOConUnaAproximadaNoSeAdivina(string destino)
    {
        var (lineas, errores) = clsMunicipiosLicitacion.Resolver([Fila(0, "Cali", destino)], _catalogo);

        errores.Should().ContainSingle(e => e.Campo == "destino"
            && (e.Codigo == clsCodigosLineaSolicitud.MunicipioNoEncontrado || e.Codigo == clsCodigosLineaSolicitud.MunicipioAmbiguo));
        lineas[0]!.Linea.DestinoMunicipioId.Should().BeNull();
    }

    [Fact]
    public void ElIdQueTraeLaFilaSeRespetaYNoNecesitaElCatalogo()
    {
        IReadOnlyList<clsLineaLicitacionEntrada?> filas = [Fila(0, "La Unión", "Macondo", origenId: 5400, destinoId: 99)];

        clsMunicipiosLicitacion.NecesitaCatalogo(filas).Should().BeFalse();
        var (lineas, errores) = clsMunicipiosLicitacion.Resolver(filas, _catalogo);
        errores.Should().BeEmpty();
        (lineas[0]!.Linea.OrigenMunicipioId, lineas[0]!.Linea.DestinoMunicipioId).Should().Be(((int?)5400, (int?)99));
    }

    [Fact]
    public void LasFilasVaciasOSinTextoNoSeResuelven()
    {
        IReadOnlyList<clsLineaLicitacionEntrada?> filas = [null, Fila(1, "", "  ")];

        clsMunicipiosLicitacion.NecesitaCatalogo(filas).Should().BeFalse();
        clsMunicipiosLicitacion.Resolver(filas, _catalogo).Errores.Should().BeEmpty("el origen vacío ya es LINEA_INCOMPLETA");
    }
}
