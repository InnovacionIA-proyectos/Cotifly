using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Concurrencia optimista sobre <c>Cotizaciones.versionNegocio</c> y el historial de PDF (script 0006, ADR-018 §8, §11 y §12) contra
/// SQL Server 2019: <see cref="IClsUnitOfWork.GuardarCambiosSiVersionAsync"/> solo confirma con la versión vigente, las escrituras que
/// no son de negocio (agenda del polling) no la cambian y una versión vencida no escribe nada.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsConcurrenciaCotizacionTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task LaVersionDeNegocioYElSugeridoSeGuardanYLaAgendaNoCambiaLaVersion()
    {
        var cotizacion = NuevaCotizacion(null);
        await GuardarAsync(cotizacion);

        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.Cotizaciones SET costeoConsultasFallidas = 3, costeoSiguienteConsultaAt = SYSDATETIMEOFFSET() WHERE id = '{cotizacion.Id}'");
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct));

        leida!.VersionNegocio.Should().Be(0, "el reclamo y la agenda del polling no son cambios de negocio");
        leida.PdfDesactualizado.Should().BeFalse();
        leida.TextosCotizacion.Should().BeNull();
        (leida.MargenSugerido, leida.OrigenMargenSugerido).Should().Be((0.15m, OrigenMargen.PoliticaLinea));
    }

    [Fact]
    public async Task ConLaVersionVigenteGuardaLaNuevaVersionYElHistorial()
    {
        var cotizacion = NuevaCotizacion(null);
        cotizacion.AsignarPdf("PCN-X.pdf");
        await GuardarAsync(cotizacion);

        var guardado = await ConsultarAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsCotizacionRepository>();
            var propia = await repo.ObtenerPorIdAsync(cotizacion.Id, Ct);
            var anterior = propia!.InvalidarPdf()!;
            repo.AgregarHistorialPdf(clsCotizacionPdfHistorial.Registrar(propia.Id, anterior, MotivoInvalidacionPdf.AjusteMargen, null));
            propia.IncrementarVersionNegocio();
            return await s.GetRequiredService<IClsUnitOfWork>().GuardarCambiosSiVersionAsync(propia.Id, 0, Ct);
        });

        guardado.Should().BeTrue();
        var fila = await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            $"SELECT CONCAT(c.versionNegocio, '|', ISNULL(c.pdfPath, '-'), '|', c.pdfDesactualizado, '|', h.pdfPath, '|', h.motivo) FROM dbo_cotifly.Cotizaciones c "
            + $"JOIN dbo_cotifly.CotizacionPdfsHistorial h ON h.cotizacionId = c.id WHERE c.id = '{cotizacion.Id}'");
        fila.Should().Be("1|-|1|PCN-X.pdf|ajuste_margen");
        var historial = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ListarHistorialPdfAsync(cotizacion.Id, Ct));
        historial.Should().ContainSingle().Which.CreatedAt.Should().Be(Reloj.AhoraUtc);
    }

    [Fact]
    public async Task ConUnaVersionVencidaNoEscribeNadaYDescartaLosCambios()
    {
        var cotizacion = NuevaCotizacion(null);
        cotizacion.AsignarPdf("PCN-Y.pdf");
        await GuardarAsync(cotizacion);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.Cotizaciones SET versionNegocio = 1 WHERE id = '{cotizacion.Id}'"); // otro flujo confirmó un cambio

        var (guardado, pendientes) = await ConsultarAsync(async s =>
        {
            var propia = await s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct);
            propia!.InvalidarPdf();
            var ok = await s.GetRequiredService<IClsUnitOfWork>().GuardarCambiosSiVersionAsync(propia.Id, 0, Ct);
            return (ok, Contexto(s).ChangeTracker.Entries().Count());
        });

        guardado.Should().BeFalse();
        pendientes.Should().Be(0, "los cambios de una versión vencida se descartan");
        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
                $"SELECT CONCAT(pdfPath, '|', pdfDesactualizado, '|', versionNegocio) FROM dbo_cotifly.Cotizaciones WHERE id = '{cotizacion.Id}'"))
            .Should().Be("PCN-Y.pdf|0|1");
    }

    [Fact]
    public async Task ElPollingQueLeyoAntesDeUnAjusteNoPisaElPrecioAjustado()
    {
        var cotizacion = NuevaCotizacion(null);
        await GuardarAsync(cotizacion);

        // El polling carga la cotización (versión 0) y calcula el precio con el margen viejo...
        await using var polling = NuevoAmbito();
        var vistaPolling = await polling.ServiceProvider.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct);
        vistaPolling!.RegistrarPrecioCosteo(1_275_000m, 1_500_000m, null, Reloj.AhoraUtc);
        vistaPolling.IncrementarVersionNegocio();

        // ...mientras el comercial confirma un ajuste de margen (versión 0 → 1).
        var ajustado = await ConsultarAsync(async s =>
        {
            var propia = await s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct);
            propia!.AplicarMargenNegociado(0.18m);
            propia.RegistrarPrecioAjustado(1_554_878m, null);
            propia.IncrementarVersionNegocio();
            return await s.GetRequiredService<IClsUnitOfWork>().GuardarCambiosSiVersionAsync(propia.Id, 0, Ct);
        });

        var pollingGuardo = await polling.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosSiVersionAsync(cotizacion.Id, 0, Ct);

        ajustado.Should().BeTrue();
        pollingGuardo.Should().BeFalse("el polling pierde y repite la consulta en el siguiente ciclo");
        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
                $"SELECT CONCAT(precioFinal, '|', margenAplicado, '|', versionNegocio) FROM dbo_cotifly.Cotizaciones WHERE id = '{cotizacion.Id}'"))
            .Should().Be("1554878.00|0.1800|1");
    }

    [Fact]
    public async Task UnaCotizacionInexistenteNuncaCoincide()
    {
        var guardado = await ConsultarAsync(s => s.GetRequiredService<IClsUnitOfWork>().GuardarCambiosSiVersionAsync(Guid.NewGuid(), 0, Ct));

        guardado.Should().BeFalse();
    }
}
