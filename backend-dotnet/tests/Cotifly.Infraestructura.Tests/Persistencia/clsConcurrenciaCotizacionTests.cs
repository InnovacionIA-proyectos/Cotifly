using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Concurrencia optimista sobre <c>Cotizaciones.rowVersion</c> y el historial de PDF (script 0006, ADR-018 §8 y §11) contra SQL
/// Server 2019: EF relee la versión tras guardar, <see cref="IClsUnitOfWork.GuardarCambiosSiVersionAsync"/> solo confirma con la
/// versión vigente y una escritura de otro (el polling) invalida la versión leída.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsConcurrenciaCotizacionTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task AlGuardarEfLeeLaRowVersionYCambiaEnCadaEscritura()
    {
        var cotizacion = NuevaCotizacion(null);

        await GuardarAsync(cotizacion);
        var primera = cotizacion.RowVersion;
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct));

        primera.Should().NotBeNullOrEmpty("EF relee la versión que asignó SQL Server");
        leida!.RowVersion.Should().Equal(primera);
        leida.PdfDesactualizado.Should().BeFalse();
        leida.TextosCotizacion.Should().BeNull();

        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.Cotizaciones SET costeoConsultasFallidas = 3 WHERE id = '{cotizacion.Id}'");
        var despues = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct));
        despues!.RowVersion.Should().NotEqual(primera!, "cualquier escritura (también la del polling) cambia la versión");
    }

    [Fact]
    public async Task ConLaVersionVigenteGuardaYDevuelveLaNueva()
    {
        var cotizacion = NuevaCotizacion(null);
        cotizacion.AsignarPdf("PCN-X.pdf");
        await GuardarAsync(cotizacion);
        var version = cotizacion.RowVersion!;

        var (guardado, nueva) = await ConsultarAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsCotizacionRepository>();
            var propia = await repo.ObtenerPorIdAsync(cotizacion.Id, Ct);
            var anterior = propia!.InvalidarPdf()!;
            repo.AgregarHistorialPdf(clsCotizacionPdfHistorial.Registrar(propia.Id, anterior, MotivoInvalidacionPdf.AjusteMargen, null));
            var ok = await s.GetRequiredService<IClsUnitOfWork>().GuardarCambiosSiVersionAsync(propia.Id, version, Ct);
            return (ok, propia.RowVersion);
        });

        guardado.Should().BeTrue();
        nueva.Should().NotEqual(version);
        var fila = await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            $"SELECT CONCAT(ISNULL(c.pdfPath, '-'), '|', c.pdfDesactualizado, '|', h.pdfPath, '|', h.motivo) FROM dbo_cotifly.Cotizaciones c "
            + $"JOIN dbo_cotifly.CotizacionPdfsHistorial h ON h.cotizacionId = c.id WHERE c.id = '{cotizacion.Id}'");
        fila.Should().Be("-|1|PCN-X.pdf|ajuste_margen");
        var historial = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ListarHistorialPdfAsync(cotizacion.Id, Ct));
        historial.Should().ContainSingle().Which.CreatedAt.Should().Be(Reloj.AhoraUtc);
    }

    [Fact]
    public async Task ConUnaVersionVencidaNoEscribeNadaYDescartaLosCambios()
    {
        var cotizacion = NuevaCotizacion(null);
        cotizacion.AsignarPdf("PCN-Y.pdf");
        await GuardarAsync(cotizacion);
        var leidaPorElComercial = cotizacion.RowVersion!;
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.Cotizaciones SET costeoConsultasFallidas = 1 WHERE id = '{cotizacion.Id}'"); // el polling escribió

        var (guardado, pendientes) = await ConsultarAsync(async s =>
        {
            var propia = await s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct);
            propia!.InvalidarPdf();
            var ok = await s.GetRequiredService<IClsUnitOfWork>().GuardarCambiosSiVersionAsync(propia.Id, leidaPorElComercial, Ct);
            return (ok, Contexto(s).ChangeTracker.Entries().Count());
        });

        guardado.Should().BeFalse();
        pendientes.Should().Be(0, "los cambios de una versión vencida se descartan");
        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
                $"SELECT CONCAT(pdfPath, '|', pdfDesactualizado) FROM dbo_cotifly.Cotizaciones WHERE id = '{cotizacion.Id}'"))
            .Should().Be("PCN-Y.pdf|0");
    }

    [Fact]
    public async Task UnaCotizacionInexistenteNuncaCoincide()
    {
        var guardado = await ConsultarAsync(s =>
            s.GetRequiredService<IClsUnitOfWork>().GuardarCambiosSiVersionAsync(Guid.NewGuid(), [0, 0, 0, 0, 0, 0, 0, 1], Ct));

        guardado.Should().BeFalse();
    }
}
