using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Folio del servidor (ADR-011) contra SQL Server 2019 real: <c>sp_getapplock</c> por iniciales y día serializa las generaciones simultáneas (del
/// mismo comercial y de varios), la transacción deshace el número si la creación falla y las versiones no se repiten.
/// Cada prueba usa su propio día (reloj falso) para no compartir consecutivos.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsFolioIntegracionTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private static int _dia;

    /// <summary>Un día distinto por prueba (18:00 UTC = 13:00 en Bogotá, mismo día).</summary>
    private string UsarDiaPropio()
    {
        var fecha = new DateTimeOffset(2041, 1, 1, 18, 0, 0, TimeSpan.Zero).AddDays(Interlocked.Increment(ref _dia));
        Reloj.AhoraUtc = fecha;
        return clsFolio.SegmentoFecha(DateOnly.FromDateTime(fecha.UtcDateTime));
    }

    [Fact]
    public async Task DiezGeneracionesSimultaneasDelMismoComercialObtienenConsecutivosUnicos()
    {
        var dia = UsarDiaPropio();
        var ana = await CrearUsuarioAsync(nombreCompleto: "Ana Duarte Gómez");

        var resultados = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => CrearAsync(ana, null)));

        resultados.Should().OnlyContain(r => r.EsExitoso, string.Join("; ", resultados.Where(r => r.EsFallo).Select(r => r.Error.Mensaje)));
        resultados.Select(r => r.Valor).Should().BeEquivalentTo(Enumerable.Range(1, 10).Select(n => $"PCN-AD-{dia}-{n:000}"));
    }

    [Fact]
    public async Task DosComercialesConInicialesDistintasEmpiezanAmbosEn001AunqueGenerenALaVez()
    {
        var dia = UsarDiaPropio();
        var ana = await CrearUsuarioAsync(nombreCompleto: "Ana Duarte Gómez");
        var luis = await CrearUsuarioAsync(nombreCompleto: "Luis Pérez Ríos");

        var resultados = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => CrearAsync(i % 2 == 0 ? ana : luis, null)));

        resultados.Should().OnlyContain(r => r.EsExitoso, string.Join("; ", resultados.Where(r => r.EsFallo).Select(r => r.Error.Mensaje)));
        resultados.Select(r => r.Valor).Should().BeEquivalentTo(
            Enumerable.Range(1, 5).SelectMany(n => new[] { $"PCN-AD-{dia}-{n:000}", $"PCN-LP-{dia}-{n:000}" }));
    }

    [Fact]
    public async Task DosComercialesConLasMismasInicialesCompartenElConsecutivoSinColisionar()
    {
        var dia = UsarDiaPropio();
        var ana = await CrearUsuarioAsync(nombreCompleto: "Ana Duarte Gómez");
        var andres = await CrearUsuarioAsync(nombreCompleto: "Andrés Díaz");

        var resultados = await Task.WhenAll(CrearAsync(ana, null), CrearAsync(andres, null));

        resultados.Should().OnlyContain(r => r.EsExitoso, string.Join("; ", resultados.Where(r => r.EsFallo).Select(r => r.Error.Mensaje)));
        resultados.Select(r => r.Valor).Should().BeEquivalentTo($"PCN-AD-{dia}-001", $"PCN-AD-{dia}-002");
    }

    [Fact]
    public async Task ElConsecutivoEsPorInicialesYCuentaLosFoliosConYSinNit()
    {
        var dia = UsarDiaPropio();
        var ana = await CrearUsuarioAsync(nombreCompleto: "Ana Duarte Gómez");
        var luis = await CrearUsuarioAsync(nombreCompleto: "Luis Pérez Ríos");

        var primera = await CrearAsync(ana, "900.123.456-7");
        var segunda = await CrearAsync(ana, null);
        var deLuis = await CrearAsync(luis, "860000111");
        var ultimo = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerUltimoConsecutivoDelDiaAsync("AD", dia, Ct));

        primera.Valor.Should().Be($"PCN-9001234567-AD-{dia}-001");
        segunda.Valor.Should().Be($"PCN-AD-{dia}-002");
        deLuis.Valor.Should().Be($"PCN-860000111-LP-{dia}-001");
        ultimo.Should().Be(2);
    }

    [Fact]
    public async Task ElUltimoConsecutivoNoCuentaVersionesNiFoliosDeOtrasInicialesODiasNiHeredados()
    {
        var dia = UsarDiaPropio();
        var ana = await CrearUsuarioAsync(nombreCompleto: "Ana Duarte Gómez");
        await GuardarAsync(NuevaCotizacion(ana, $"PCN-AD-{dia}-004"));
        await GuardarAsync(clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = $"PCN-AD-{dia}-004-v2", Version = 2, UsuarioId = ana }).Valor);
        await GuardarAsync(NuevaCotizacion(ana, $"PCN-800111222-AD-{dia}-006"));
        await GuardarAsync(NuevaCotizacion(ana, $"PCN-ZAD-{dia}-900"));
        await GuardarAsync(NuevaCotizacion(ana, $"PCN-LP-{dia}-950"));
        await GuardarAsync(NuevaCotizacion(ana, $"PCN-{dia}-990"));

        var ultimo = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerUltimoConsecutivoDelDiaAsync("AD", dia, Ct));
        var siguiente = await CrearAsync(ana, null);

        ultimo.Should().Be(6);
        siguiente.Valor.Should().Be($"PCN-AD-{dia}-007");
    }

    [Fact]
    public async Task SiLaCreacionFallaLaTransaccionSeDeshaceYElNumeroQuedaLibre()
    {
        var dia = UsarDiaPropio();
        var ana = await CrearUsuarioAsync();

        await using (var ambito = NuevoAmbito())
        {
            var servicios = ambito.ServiceProvider;
            var fallida = await servicios.GetRequiredService<IClsGeneradorFolioService>().CrearConFolioNuevoAsync<string>(ana, null, async (folio, ct) =>
            {
                servicios.GetRequiredService<IClsCotizacionRepository>().Agregar(NuevaCotizacion(ana, folio.Valor));
                await servicios.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(ct);
                return new clsErrorDominio(clsCodigosError.PdfRenderNoDisponible, "falla después de guardar");
            }, Ct);
            fallida.Error.Codigo.Should().Be(clsCodigosError.PdfRenderNoDisponible);
        }

        var siguiente = await CrearAsync(ana, null);

        siguiente.Valor.Should().Be($"PCN-CP-{dia}-001", "la fila de la creación fallida se deshizo con la transacción");
    }

    [Fact]
    public async Task VersionesSimultaneasDeUnaCotizacionObtienenNumerosUnicosSobreLaBase()
    {
        var dia = UsarDiaPropio();
        var ana = await CrearUsuarioAsync();
        var v1 = await CrearAsync(ana, null);
        var idV1 = (await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(v1.Valor, Ct)))!.Id;

        var versiones = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => CrearVersionAsync(ana, idV1)));
        var maxima = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerVersionMaximaAsync(v1.Valor, Ct));
        var columnaVersion = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            $"SELECT version FROM dbo_cotifly.Cotizaciones WHERE folio = '{v1.Valor}-v6'");

        versiones.Should().OnlyContain(r => r.EsExitoso);
        versiones.Select(r => r.Valor).Should().BeEquivalentTo(Enumerable.Range(2, 5).Select(n => $"PCN-CP-{dia}-001-v{n}"));
        maxima.Should().Be(6);
        columnaVersion.Should().Be(6);
    }

    [Fact]
    public async Task LaVersionNuevaDeUnFolioHeredadoSinInicialesEsV2SobreSuBase()
    {
        var dia = UsarDiaPropio();
        var ana = await CrearUsuarioAsync(nombreCompleto: "Ana Duarte Gómez");
        var heredada = NuevaCotizacion(ana, $"PCN-{dia}-001");
        await GuardarAsync(heredada);

        var v2 = await CrearVersionAsync(ana, heredada.Id);
        var columnaVersion = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            $"SELECT version FROM dbo_cotifly.Cotizaciones WHERE folio = 'PCN-{dia}-001-v2'");

        v2.Valor.Should().Be($"PCN-{dia}-001-v2");
        columnaVersion.Should().Be(2);
    }

    [Fact]
    public async Task LaVersionMaximaDeUnFolioHeredadoEscapaLosComodinesDeLike()
    {
        var ana = await CrearUsuarioAsync();
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        await GuardarAsync(NuevaCotizacion(ana, $"LEG_{sufijo}"));
        await GuardarAsync(clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = $"LEG_{sufijo}-v3", Version = 3, UsuarioId = ana }).Valor);
        await GuardarAsync(clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = $"LEGX{sufijo}-v9", Version = 9, UsuarioId = ana }).Valor);

        var maxima = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerVersionMaximaAsync($"LEG_{sufijo}", Ct));

        maxima.Should().Be(3, "el '_' del folio heredado no debe comportarse como comodín");
    }

    [Fact]
    public async Task ElCandadoNoSeAnidaDentroDeOtraTransaccion()
    {
        await using var ambito = NuevoAmbito();
        var candado = ambito.ServiceProvider.GetRequiredService<IClsCandadoTransaccional>();

        var anidar = () => candado.EjecutarAsync<int>("cotifly:prueba", _ =>
            candado.EjecutarAsync<int>("cotifly:prueba-2", _ => Task.FromResult(clsResultado<int>.Exito(1)), Ct), Ct);

        await anidar.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ElListadoConLineaTraeElNombreDeLaLineaYSoloLasDelComercial()
    {
        var ana = await CrearUsuarioAsync();
        var luis = await CrearUsuarioAsync();
        var conLinea = NuevaCotizacion(ana);
        var sinLinea = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = FolioUnico(), UsuarioId = ana }).Valor;
        await GuardarAsync(conLinea);
        Reloj.Avanzar(TimeSpan.FromSeconds(1));
        await GuardarAsync(sinLinea);
        await GuardarAsync(NuevaCotizacion(luis));

        var filas = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ListarConLineaPorUsuarioAsync(ana, 100, Ct));

        filas.Select(f => (f.Cotizacion.Folio, f.LineaNegocio)).Should().Equal((sinLinea.Folio, (string?)null), (conLinea.Folio, "Nacional"));
    }

    private async Task<clsResultado<string>> CrearAsync(Guid usuarioId, string? nit)
    {
        await using var ambito = NuevoAmbito();
        var servicios = ambito.ServiceProvider;
        return await servicios.GetRequiredService<IClsGeneradorFolioService>().CrearConFolioNuevoAsync<string>(usuarioId, nit, async (folio, ct) =>
        {
            servicios.GetRequiredService<IClsCotizacionRepository>().Agregar(NuevaCotizacion(usuarioId, folio.Valor));
            await servicios.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(ct);
            return folio.Valor;
        }, Ct);
    }

    private async Task<clsResultado<string>> CrearVersionAsync(Guid usuarioId, Guid origen)
    {
        await using var ambito = NuevoAmbito();
        var servicios = ambito.ServiceProvider;
        return await servicios.GetRequiredService<IClsGeneradorFolioService>().CrearNuevaVersionAsync<string>(usuarioId, origen, async (folio, ct) =>
        {
            var nueva = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = folio.Valor, Version = folio.Version, UsuarioId = usuarioId }).Valor;
            servicios.GetRequiredService<IClsCotizacionRepository>().Agregar(nueva);
            await servicios.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(ct);
            return folio.Valor;
        }, Ct);
    }
}
