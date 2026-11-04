using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Con el registro real (<c>clsFuenteMargenTablaLocal</c>, HU-042.2) y una copia local que nunca se sincronizó (la base compartida de
/// la colección no tiene ninguna corrida <c>ok</c> en <c>SapMargenSyncLog</c>, como un ambiente con la sincronización del backend deshabilitada), un cliente con BP
/// obtiene la política de la línea y no un error: "no configurado" no es "caído".
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMargenSapNoConfiguradoIntegracionTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task ConBpYSinFuenteConfiguradaSeUsaLaPoliticaDeLaLinea()
    {
        var resultado = await ConsultarAsync(s =>
            s.GetRequiredService<IClsMargenResolverService>().ResolverAsync(new clsEntradaMargen("Nacional", "15927"), Ct));

        var salida = resultado.Valor.Salida;
        salida.Origen.Should().Be("politica_linea");
        salida.Margen.Should().Be(0.15m);
        salida.TrqType.Should().Be("OPTN");
        salida.Detalle.Should().Contain("SAP TM no configurado: se usa la política de la línea");
    }
}
